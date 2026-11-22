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

    private static bool inFront;

    private static Intent? waiting;

    private static Action? launched;

    /// <summary>
    /// Gets a value indicating whether the app is in front of the user, which is the only time Android lets it open another screen such as the installer.
    /// </summary>
    public static bool IsInFront => inFront;

    /// <summary>
    /// Keeps the installer of an update until the app is in front again, when it is opened.
    /// </summary>
    /// <param name="install">The intent that opens the installer.</param>
    /// <param name="opened">Called once the installer was opened.</param>
    public static void Defer(Intent install, Action opened)
    {
        waiting = install;
        launched = opened;
    }

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
    protected override void OnResume()
    {
        base.OnResume();
        inFront = true;

        if (waiting is { } install)
        {
            Action? opened = launched;
            waiting = null;
            launched = null;
            StartActivity(install);
            opened?.Invoke();
        }
    }

    /// <inheritdoc />
    protected override void OnPause()
    {
        inFront = false;
        base.OnPause();
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
