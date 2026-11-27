namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Offers the tools of several parts of the server to a session.
/// </summary>
/// <param name="parts">The parts.</param>
internal sealed class CombinedSessionTools(IReadOnlyList<ISessionTools> parts) : ISessionTools
{
    /// <inheritdoc />
    public async Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation)
    {
        foreach (ISessionTools part in parts)
        {
            await part.Offer(workspace, session, cancellation);
        }
    }

    /// <inheritdoc />
    public bool Keeps(Guid workspaceId) => parts.Any(part => part.Keeps(workspaceId));
}
