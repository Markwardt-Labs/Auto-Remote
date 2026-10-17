namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Keeps the app and its connection alive while work this client gave a workspace is running, so the app is still listening when the work ends even if the user left it; each head that is stopped in the background implements it with its platform's way.
/// </summary>
public interface IWorkKeeper
{
    /// <summary>
    /// Starts keeping the app alive for a workspace that was given work; repeating it for the same workspace changes nothing.
    /// </summary>
    /// <param name="workspace">The workspace identifier.</param>
    /// <param name="name">The workspace's name, which may be shown to the user while the work runs.</param>
    void Hold(Guid workspace, string name);

    /// <summary>
    /// Stops keeping the app alive for a workspace, which happens once its work ended or never started; the app is let go of when no workspace is held. Does nothing for a workspace that is not held.
    /// </summary>
    /// <param name="workspace">The workspace identifier.</param>
    void Release(Guid workspace);
}
