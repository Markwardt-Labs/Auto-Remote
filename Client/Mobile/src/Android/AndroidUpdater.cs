using Android.Content;
using Android.Provider;
using AndroidX.Core.Content;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Updates the Android client by downloading the package of the release and handing it to the system installer, which asks the user to confirm and replaces the app, provided it is signed with the same key. The first time, the user has to allow this app to install packages in the settings.
/// </summary>
/// <param name="feed">Downloads the package.</param>
internal sealed class AndroidUpdater(IReleaseFeed feed) : IClientUpdater
{
    private readonly Context context = global::Android.App.Application.Context;

    private readonly string assetName = "AutoRemote-android.apk";

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task Install(ReleaseInfo release, IProgress<double> progress, CancellationToken cancellation)
    {
        ReleaseAsset asset = release.Assets.FirstOrDefault(candidate => candidate.Name == assetName) ?? throw new IOException($"Release {release.Version} has no {assetName} file.");

        if (OperatingSystem.IsAndroidVersionAtLeast(26) && context.PackageManager is { } packages && !packages.CanRequestPackageInstalls())
        {
            Intent settings = new Intent(Settings.ActionManageUnknownAppSources, global::Android.Net.Uri.Parse($"package:{context.PackageName}")).AddFlags(ActivityFlags.NewTask)!;
            context.StartActivity(settings);
            throw new InvalidOperationException("Allow Auto Remote to install apps in the settings that just opened, then go back and update again.");
        }

        string folder = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "updates");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, assetName);
        await feed.Download(asset, path, progress, cancellation);
        global::Android.Net.Uri uri = FileProvider.GetUriForFile(context, $"{context.PackageName}.updates", new Java.IO.File(path))!;
        Intent install = new Intent(Intent.ActionView).SetDataAndType(uri, "application/vnd.android.package-archive")!.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask)!;
        context.StartActivity(install);
    }
}
