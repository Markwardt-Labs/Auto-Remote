using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The Android activity that hosts the Avalonia application.
/// </summary>
[Activity(Label = "Auto Remote", Icon = "@mipmap/appicon", Theme = "@style/Theme.AppCompat.NoActionBar", MainLauncher = true, ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode, LaunchMode = LaunchMode.SingleTop)]
public sealed class MainActivity : AvaloniaMainActivity
{
    /// <summary>
    /// Gets the name of the intent extra that carries the identifier of the workspace a notification opens.
    /// </summary>
    public static string WorkspaceKey => "workspace";

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Open(Intent);

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        }
    }

    /// <inheritdoc />
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Open(intent);
    }

    private void Open(Intent? intent)
    {
        if (intent?.GetStringExtra(WorkspaceKey) is { } text && Guid.TryParse(text, out Guid workspace))
        {
            intent.RemoveExtra(WorkspaceKey);
            (Avalonia.Application.Current as App)?.OpenWorkspace(workspace);
        }
    }
}
