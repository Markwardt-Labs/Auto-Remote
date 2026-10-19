namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Tells the user, outside the app, that a workspace they gave work to is ready again; each head that can show notifications implements it with its platform's.
/// </summary>
public interface IReadyNotifier
{
    /// <summary>
    /// Shows a notification saying the workspace is ready; a platform that cannot show one, or where the user did not allow it, does nothing. Each workspace has its own notification, and activating it is meant to open that workspace.
    /// </summary>
    /// <param name="workspaceId">The workspace identifier.</param>
    /// <param name="workspace">The workspace's name.</param>
    void Notify(Guid workspaceId, string workspace);
}
