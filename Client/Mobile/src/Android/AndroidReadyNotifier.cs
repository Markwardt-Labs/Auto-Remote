namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Shows an Android notification saying a workspace is ready; where the user has not allowed notifications the system drops it.
/// </summary>
internal sealed class AndroidReadyNotifier : IReadyNotifier
{
    private readonly string channelId = "workspace-ready";

    private readonly Context context = global::Android.App.Application.Context;

    /// <inheritdoc />
    public void Clear(Guid workspaceId) => (context.GetSystemService(Context.NotificationService) as NotificationManager)?.Cancel(workspaceId.GetHashCode());

    /// <inheritdoc />
    public void Notify(Guid workspaceId, string workspace) => Show(workspaceId, workspaceId.GetHashCode(), $"{workspace} is ready");

    /// <inheritdoc />
    public void Announce(Guid workspaceId, string message) => Show(workspaceId, message.GetHashCode(StringComparison.Ordinal), message);

    private void Show(Guid workspaceId, int id, string title)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        Notification.Builder builder;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            manager.CreateNotificationChannel(new NotificationChannel(channelId, "Workspace ready", NotificationImportance.Default) { Description = "Tells you when a workspace you gave work to is ready again" });
            builder = new Notification.Builder(context, channelId);
        }
        else
        {
#pragma warning disable CS0618
            builder = new Notification.Builder(context);
#pragma warning restore CS0618
        }

        Intent launch = new Intent(context, typeof(MainActivity))
            .SetAction($"{context.PackageName}.OPEN_WORKSPACE.{workspaceId:N}")!
            .PutExtra(MainActivity.WorkspaceKey, workspaceId.ToString())!
            .AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop)!;
        PendingIntent? open = PendingIntent.GetActivity(context, workspaceId.GetHashCode(), launch, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        Notification notification = builder
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle(title)!
            .SetAutoCancel(true)!
            .SetContentIntent(open)!
            .Build()!;
        manager.Notify(id, notification);
    }
}
