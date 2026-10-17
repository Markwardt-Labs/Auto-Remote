using Android.App;
using Android.Content;

namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The ongoing notification that shows while the app is kept alive for work it gave a workspace.
/// </summary>
internal interface IAndroidWorkNotification
{
    /// <summary>
    /// Gets the identifier the notification is shown with.
    /// </summary>
    int Id { get; }

    /// <summary>
    /// Builds the notification.
    /// </summary>
    /// <param name="text">What it says: which workspaces are working.</param>
    /// <returns>The notification, or <see langword="null" /> when Android offers no way to build one.</returns>
    Notification? Build(string text);

    /// <summary>
    /// Replaces the text of the notification that is showing.
    /// </summary>
    /// <param name="text">What it says.</param>
    void Show(string text);
}

/// <inheritdoc />
internal sealed class AndroidWorkNotification(Context context) : IAndroidWorkNotification
{
    private readonly string channelId = "workspace-work";

    /// <inheritdoc />
    public int Id { get; } = 7001;

    /// <inheritdoc />
    public Notification? Build(string text)
    {
        Notification.Builder builder;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            (context.GetSystemService(Context.NotificationService) as NotificationManager)?.CreateNotificationChannel(new NotificationChannel(channelId, "Workspace work", NotificationImportance.Low) { Description = "Shown while a workspace you gave work to is working, which keeps the app connected" });
            builder = new Notification.Builder(context, channelId);
        }
        else
        {
#pragma warning disable CS0618
            builder = new Notification.Builder(context);
#pragma warning restore CS0618
        }

        Intent? launch = context.PackageManager?.GetLaunchIntentForPackage(context.PackageName ?? string.Empty);
        PendingIntent? open = launch is null ? null : PendingIntent.GetActivity(context, 0, launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        return builder
            .SetSmallIcon(context.ApplicationInfo?.Icon ?? 0)!
            .SetContentTitle("Auto Remote")!
            .SetContentText(text)!
            .SetOngoing(true)!
            .SetContentIntent(open)!
            .Build();
    }

    /// <inheritdoc />
    public void Show(string text)
    {
        if (Build(text) is { } notification)
        {
            (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Notify(Id, notification);
        }
    }
}
