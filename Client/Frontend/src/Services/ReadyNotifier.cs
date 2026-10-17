namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Tells the user, outside the app, that a workspace they gave work to is ready again; each head that can show notifications implements it with its platform's.
/// </summary>
public interface IReadyNotifier
{
    /// <summary>
    /// Shows a notification saying the workspace is ready; a platform that cannot show one, or where the user did not allow it, does nothing.
    /// </summary>
    /// <param name="workspace">The workspace's name.</param>
    void Notify(string workspace);
}
