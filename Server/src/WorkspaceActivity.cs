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
    /// Goes from one state straight into another, so that no other work finds the workspace ready in between.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="from">The state it must be in.</param>
    /// <param name="to">The state it goes into.</param>
    /// <returns><see langword="true" /> if it was in <paramref name="from" />.</returns>
    bool Pass(Guid id, WorkspaceState from, WorkspaceState to);

    /// <summary>
    /// Moves a workspace that is working or managing into <see cref="WorkspaceState.Stopping" /> or <see cref="WorkspaceState.Canceling" />, which no other work can leave until it calls <see cref="Leave" />.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <param name="halting">The state to enter: <see cref="WorkspaceState.Stopping" /> or <see cref="WorkspaceState.Canceling" />.</param>
    /// <returns>The state the workspace was in, or <see langword="null" /> when it was ready or already stopping or canceling, and nothing changed.</returns>
    WorkspaceState? Halt(Guid id, WorkspaceState halting);

    /// <summary>
    /// Runs an action while no workspace changes its state. A caller that holds a lock of its own while it reads or changes states takes that lock inside the action: a change of state is published while the lock of the states is held, to subscribers that may take the caller's, so the two are always taken in this order.
    /// </summary>
    /// <typeparam name="T">The type of the result.</typeparam>
    /// <param name="action">The action, which must not wait for anything.</param>
    /// <returns>What the action returns.</returns>
    T Together<T>(Func<T> action);

    /// <summary>
    /// Describes what holds a workspace, for the refusal of work that cannot start.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    /// <returns>A sentence naming the work in progress.</returns>
    string Describe(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceActivity(IEventHub events) : IWorkspaceActivity
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
                    Publish(id, state);
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
            if (states.GetValueOrDefault(id) == state && states.Remove(id))
            {
                Publish(id, WorkspaceState.Ready);
            }
        }
    }

    /// <inheritdoc />
    public bool Pass(Guid id, WorkspaceState from, WorkspaceState to)
    {
        lock (gate)
        {
            if (states.GetValueOrDefault(id) != from || from == WorkspaceState.Ready)
            {
                return false;
            }

            states[id] = to;
            Publish(id, to);
            return true;
        }
    }

    /// <inheritdoc />
    public WorkspaceState? Halt(Guid id, WorkspaceState halting)
    {
        lock (gate)
        {
            WorkspaceState current = states.GetValueOrDefault(id);

            if (current is not (WorkspaceState.Working or WorkspaceState.Managing))
            {
                return null;
            }

            states[id] = halting;
            Publish(id, halting);
            return current;
        }
    }

    /// <inheritdoc />
    public T Together<T>(Func<T> action)
    {
        lock (gate)
        {
            return action();
        }
    }

    /// <inheritdoc />
    public string Describe(Guid id) => Get(id) switch
    {
        WorkspaceState.Working => "The AI is working in this workspace right now.",
        WorkspaceState.Managing => "A git action is running in this workspace right now.",
        WorkspaceState.Stopping => "The work in this workspace is being stopped right now.",
        WorkspaceState.Canceling => "The work in this workspace is being cancelled and reverted right now.",
        _ => "The workspace is ready."
    };

    // Published while the lock is held so events leave in the order the states changed. Subscribers run inside it, which Together is there for.
    private void Publish(Guid id, WorkspaceState state) => events.Publish(new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = id, State = state });
}
