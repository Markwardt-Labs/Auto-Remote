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
    private static MainActivity? current;

    private static TaskCompletionSource<bool>? microphone;

    private static readonly int microphoneRequest = 2;

    /// <summary>
    /// Gets the name of the intent extra that carries the identifier of the workspace a notification opens.
    /// </summary>
    public static string WorkspaceKey => "workspace";

    /// <summary>
    /// Makes sure the app may use the microphone, asking the user if it has not been decided yet.
    /// </summary>
    /// <returns>Whether the microphone may be used.</returns>
    public static Task<bool> AskForMicrophone()
    {
        if (current is not { } activity)
        {
            return Task.FromResult(false);
        }

        if (activity.CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        microphone?.TrySetResult(false);
        TaskCompletionSource<bool> asking = new(TaskCreationOptions.RunContinuationsAsynchronously);
        microphone = asking;
        activity.RunOnUiThread(() => activity.RequestPermissions([Manifest.Permission.RecordAudio], microphoneRequest));
        return asking.Task;
    }

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        current = this;
        Open(Intent);

        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
        }
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        if (current == this)
        {
            current = null;
        }

        base.OnDestroy();
    }

    /// <inheritdoc />
    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == microphoneRequest)
        {
            microphone?.TrySetResult(grantResults.Length > 0 && grantResults[0] == Permission.Granted);
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
