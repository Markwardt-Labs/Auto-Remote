namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tool with which a workspace's AI confirms what it brought in: side sessions and the results of delegated tasks. Every workspace has it, since tools cannot be added to work that is running.
/// </summary>
/// <param name="integrations">Gives the integrations, which are made after the tools.</param>
/// <param name="delegated">Tells whether a workspace runs a task another server delegated, which brings nothing in.</param>
internal sealed class SideSessionTools(Func<ISideIntegrations> integrations, Func<Guid, bool> delegated) : ISessionTools
{
    private readonly ConcurrentDictionary<Guid, PromptTool> made = new();

    private readonly string finishName = "finish_integration";

    /// <inheritdoc />
    public Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation)
    {
        // The same tool is offered every time, so a session that already serves it does not restart its process.
        PromptTool? wanted = delegated(workspace.Id) ? null : made.GetOrAdd(workspace.Id, Finish);
        IList<PromptTool> offered = session.Transient.Tools;

        foreach (PromptTool other in offered.Where(tool => tool.Name == finishName && !ReferenceEquals(tool, wanted)).ToList())
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
        => PromptTool.Create<FinishIntegrationInput, string>(finishName, "Confirms that you brought something into this workspace, once all of its changes and intent are brought over: a side session or a delegated result, which is then removed for good. Only use it for a request to bring something in, with an integration id the request gave.", (input, _) => integrations().Finish(mainId, input.IntegrationId ?? string.Empty));
}
