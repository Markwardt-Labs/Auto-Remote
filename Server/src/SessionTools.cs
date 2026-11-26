namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Offers the server's own tools to the AI of a workspace's session, as far as the workspace allows them.
/// </summary>
internal interface ISessionTools
{
    /// <summary>
    /// Adds the tools the workspace may use to its session, or takes them away; a session that already has what it should is left alone. A change applies from the session's next prompt.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="session">Its session.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>A task that completes once done.</returns>
    Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation);

    /// <summary>
    /// Tells whether the session a workspace has must not be replaced, because what it may do was set on it and a new one would start with the owner's defaults.
    /// </summary>
    /// <param name="workspaceId">The workspace.</param>
    /// <returns><see langword="true" /> if only the stored session may be used.</returns>
    bool Keeps(Guid workspaceId);
}
