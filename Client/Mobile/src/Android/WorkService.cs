using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The foreground service that keeps the app, and so its connection to the server, alive while work this app gave a workspace runs. Android lets a foreground service run in the background as long as it shows its notification; it stops itself after twelve hours so a workspace that never answers cannot keep it forever.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class WorkService : Service
{
    private readonly TimeSpan limit = TimeSpan.FromHours(12);

    private Handler? handler;

    /// <inheritdoc />
    public override IBinder? OnBind(Intent? intent) => null;

    /// <inheritdoc />
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        AndroidWorkNotification notification = new(this);

        if (notification.Build(intent?.GetStringExtra(AndroidWorkKeeper.TextKey) ?? "A workspace is working") is { } built)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(notification.Id, built, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(notification.Id, built);
            }
        }

        handler ??= new Handler(Looper.MainLooper!);
        handler.RemoveCallbacksAndMessages(null);
        handler.PostDelayed(() => StopSelf(), (long)limit.TotalMilliseconds);
        return StartCommandResult.NotSticky;
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        handler?.RemoveCallbacksAndMessages(null);
        base.OnDestroy();
    }
}
