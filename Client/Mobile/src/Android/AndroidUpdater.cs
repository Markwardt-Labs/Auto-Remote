namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Updates the Android client by downloading the package of the release and handing it to the system installer, which asks the user to confirm and replaces the app, provided it is signed with the same key. The first time, the user has to allow this app to install packages in the settings. Android does not let an app that is not in front open the installer, so when the download ends while the user is elsewhere the installer waits for the user in a notification and opens by itself when the app is in front again.
/// </summary>
/// <param name="feed">Downloads the package.</param>
internal sealed class AndroidUpdater(IReleaseFeed feed) : IClientUpdater
{
    private readonly Context context = global::Android.App.Application.Context;

    private readonly string assetName = "AutoRemote-android.apk";

    private readonly string channelId = "app-update";

    private readonly int noticeId = 2;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public async Task Install(ReleaseInfo release, IProgress<double> progress, Func<Task> downloaded, CancellationToken cancellation)
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
        await downloaded();
        Offer(path);
    }

    /// <summary>
    /// Hands the downloaded package to the system installer: at once when the app is in front, which is the only time Android lets it open the installer, and otherwise through a notification and when the app comes to the front again.
    /// </summary>
    /// <param name="path">The downloaded package.</param>
    internal void Offer(string path)
    {
        global::Android.Net.Uri uri = FileProvider.GetUriForFile(context, $"{context.PackageName}.updates", new Java.IO.File(path))!;
        Intent install = new Intent(Intent.ActionView).SetDataAndType(uri, "application/vnd.android.package-archive")!.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask)!;

        if (MainActivity.IsInFront)
        {
            context.StartActivity(install);
            return;
        }

        MainActivity.Defer(install, ClearNotice);
        Announce(install);
    }

    private void Announce(Intent install)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        Notification.Builder builder;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            manager.CreateNotificationChannel(new NotificationChannel(channelId, "App update", NotificationImportance.High) { Description = "Tells you when an update of Auto Remote is ready to install" });
            builder = new Notification.Builder(context, channelId);
        }
        else
        {
#pragma warning disable CS0618
            builder = new Notification.Builder(context);
#pragma warning restore CS0618
        }

        PendingIntent? open = PendingIntent.GetActivity(context, noticeId, install, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        Notification notification = builder
            .SetSmallIcon(context.ApplicationInfo?.Icon ?? 0)!
            .SetContentTitle("Auto Remote update ready")!
            .SetContentText("Tap to install it.")!
            .SetAutoCancel(true)!
            .SetContentIntent(open)!
            .Build()!;
        manager.Notify(noticeId, notification);
    }

    private void ClearNotice()
    {
        if (context.GetSystemService(Context.NotificationService) is NotificationManager manager)
        {
            manager.Cancel(noticeId);
        }
    }
}
