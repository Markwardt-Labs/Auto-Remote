namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Tracks which <see cref="WorkspaceState" /> each workspace is in and lets only one kind of work run in a workspace at a time.
/// </summary>
internal interface IWorkspaceActivity
{
    /// <summary>
    /// Gets the state a workspace is in.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>The state; <see cref="WorkspaceState.Ready" /> for a workspace nothing runs in.</returns>
    WorkspaceState Get(Guid id);

    /// <summary>
    /// Moves a workspace out of <see cref="WorkspaceState.Ready" /> into a state. Only <see cref="WorkspaceState.Working" /> can be entered from another state: while already working because more AI input joins that work, and while <see cref="WorkspaceState.Managing" /> without changing the state because git uses the AI to resolve conflicts.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="state">The state to enter.</param>
    /// <returns><see langword="true" /> if the workspace is now in the state, or in <see cref="WorkspaceState.Managing" /> for the AI work of a git action; <see langword="false" /> if other work holds the workspace.</returns>
    bool TryEnter(Guid id, WorkspaceState state);

    /// <summary>
    /// Returns a workspace to <see cref="WorkspaceState.Ready" /> once the work that entered a state has ended; does nothing when the workspace is in a different state.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="state">The state the ending work entered.</param>
    void Leave(Guid id, WorkspaceState state);

    /// <summary>
    /// Describes what holds a workspace, for the refusal of work that cannot start.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A sentence naming the work in progress.</returns>
    string Describe(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceActivity : IWorkspaceActivity
{
    private readonly Dictionary<Guid, WorkspaceState> states = [];

    private readonly Lock gate = new();

    /// <inheritdoc />
    public WorkspaceState Get(Guid id)
    {
        lock (gate)
        {
            return states.GetValueOrDefault(id);
        }
    }

    /// <inheritdoc />
    public bool TryEnter(Guid id, WorkspaceState state)
    {
        lock (gate)
        {
            WorkspaceState current = states.GetValueOrDefault(id);

            if (current == WorkspaceState.Ready)
            {
                if (state != WorkspaceState.Ready)
                {
                    states[id] = state;
                }

                return true;
            }

            return state == WorkspaceState.Working && current is WorkspaceState.Working or WorkspaceState.Managing;
        }
    }

    /// <inheritdoc />
    public void Leave(Guid id, WorkspaceState state)
    {
        lock (gate)
        {
            if (states.GetValueOrDefault(id) == state)
            {
                states.Remove(id);
            }
        }
    }

    /// <inheritdoc />
    public string Describe(Guid id) => Get(id) switch
    {
        WorkspaceState.Working => "The AI is working in this workspace right now.",
        WorkspaceState.Running => "A script is running in this workspace right now.",
        WorkspaceState.Managing => "A git action is running in this workspace right now.",
        _ => "The workspace is ready."
    };
}
