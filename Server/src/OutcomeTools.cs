namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The tool every workspace's AI has to say that it could not finish what it was asked, so unfinished work is not committed or taken for done.
/// </summary>
/// <param name="completion">Takes the word for the work under way.</param>
/// <param name="reported">Also told, as the swarm is for the work of a delegated task.</param>
internal sealed class OutcomeTools(Func<IWorkCompletion> completion, Func<Guid, string, Task>? reported = null) : ISessionTools
{
    private readonly ConcurrentDictionary<Guid, PromptTool> made = new();

    private readonly string name = "report_unfinished";

    /// <inheritdoc />
    public Task Offer(WorkspaceInfo workspace, IPromptSession session, CancellationToken cancellation)
    {
        // The same tool is offered every time, so a session that already serves it does not restart its process.
        PromptTool tool = made.GetOrAdd(workspace.Id, Make);
        IList<PromptTool> offered = session.Transient.Tools;

        if (!offered.Contains(tool))
        {
            foreach (PromptTool other in offered.Where(other => other.Name == name).ToList())
            {
                offered.Remove(other);
            }

            offered.Add(tool);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool Keeps(Guid workspaceId) => false;

    private PromptTool Make(Guid workspace) => PromptTool.Create<UnfinishedInput, string>(name, "Says that you could not finish what the current message asks for, such as because something is missing, broken or only the user can decide it. Nothing is committed as finished then, and the user sees your reason. Call it before your final reply, and still say in the reply what you did.", async (input, _) =>
    {
        string told = completion().Unfinished(workspace, input.Reason ?? string.Empty);

        if (reported is not null)
        {
            await reported(workspace, input.Reason ?? string.Empty);
        }

        return told;
    });
}
