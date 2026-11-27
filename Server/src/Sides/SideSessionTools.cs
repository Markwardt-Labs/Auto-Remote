namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tools of side sessions: a main workspace's AI always has <c>finish_integration</c>, since tools cannot be added to work that is running and a request to integrate joins it; a side session's AI has <c>mark_task_complete</c>.
/// </summary>
/// <param name="integrations">Gives the integrations, which are made after the tools.</param>
/// <param name="delegated">Tells whether a workspace runs a task another server delegated, which has no side sessions.</param>
internal sealed class SideSessionTools(Func<ISideIntegrations> integrations, Func<Guid, bool> delegated) : ISessionTools
{
    private readonly ConcurrentDictionary<Guid, PromptTool> made = new();

    private readonly string finishName = "finish_integration";

    private readonly string completeName = "mark_task_complete";

    /// <inheritdoc />
    public Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation)
    {
        // The same tool is offered every time, so a session that already serves it does not restart its process.
        PromptTool? wanted = workspace.Side is null && delegated(workspace.Id) ? null : made.GetOrAdd(workspace.Id, _ => workspace.Side is not null ? Complete(workspace.Id) : Finish(workspace.Id));
        IList<PromptTool> offered = session.Transient.Tools;

        foreach (PromptTool other in offered.Where(tool => (tool.Name == finishName || tool.Name == completeName) && !ReferenceEquals(tool, wanted)).ToList())
        {
            offered.Remove(other);
        }

        if (wanted is not null && !offered.Contains(wanted))
        {
            offered.Add(wanted);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool Keeps(Guid workspaceId) => false;

    private PromptTool Finish(Guid mainId)
        => PromptTool.Create<FinishIntegrationInput, string>(finishName, "Finishes the integration of a side session into this workspace, once all of its changes and intent are brought over: the side session is then removed for good. Only use it for a request to integrate, with the integration id the request gave.", (input, _) => integrations().Finish(mainId, input.IntegrationId ?? string.Empty));

    private PromptTool Complete(Guid id)
        => PromptTool.Create<SideSessionNoInput, string>(completeName, "Marks the task of this side session as completely done. Call it only once nothing is left to do. If the user set the session to integrate when finished, it is then brought into the main session by itself.", (_, _) => integrations().MarkComplete(id));
}
