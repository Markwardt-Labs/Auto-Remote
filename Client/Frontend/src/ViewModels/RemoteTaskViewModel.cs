namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A task delegated from the workspace on screen, opened from its session list: what it asks, its report and its conversation, followed live and read only, with what can be done with it.
/// </summary>
internal sealed class RemoteTaskViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RemoteTaskViewModel" /> class and starts following the task.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="task">The task as the list knows it.</param>
    /// <param name="destination">The name of the side session the task was delegated from, or <see langword="null" /> when it was delegated from the main one.</param>
    public RemoteTaskViewModel(IServerConnection connection, SwarmTaskInfo task, string? destination)
    {
        this.connection = connection;
        this.destination = destination;
        this.task = task;
        BringInCommand = new AsyncCommand(_ => Act(() => connection.BringInSwarmTask(task.Id, CancellationToken.None)));
        ContinueCommand = new AsyncCommand(_ => Continue());
        ToggleWhenDoneCommand = new AsyncCommand(_ => Act(() => connection.SetSwarmTaskBringIn(task.Id, !this.task.BringIn, CancellationToken.None)));
        CancelTaskCommand = new AsyncCommand(_ => Act(() => connection.CancelSwarmTask(task.Id, CancellationToken.None)));
        RetryCommand = new AsyncCommand(_ => Act(() => connection.RetrySwarmTask(task.Id, CancellationToken.None)));
        DiscardCommand = new AsyncCommand(async _ =>
        {
            if (await Act(() => connection.DiscardSwarmTask(task.Id, CancellationToken.None)))
            {
                Close();
            }
        });
        CloseCommand = new AsyncCommand(_ =>
        {
            Close();
            return Task.CompletedTask;
        });
        connection.EventReceived += Receive;
        connection.Reconnected += Resync;
    }

    private readonly IServerConnection connection;

    private readonly string? destination;

    private SwarmTaskInfo task;

    private SwarmTaskDetail? detail;

    private string? outcome;

    private bool reading;

    private bool readAgain;

    private bool closed;

    /// <summary>
    /// Occurs once the task should no longer be on screen: it was closed or is gone.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Occurs when a side session was made to go on with the task by hand.
    /// </summary>
    public event Action<WorkspaceInfo>? Continued;

    /// <summary>
    /// Gets the task.
    /// </summary>
    public Guid Id => task.Id;

    /// <summary>
    /// Gets everything about the task, or <see langword="null" /> until it is read.
    /// </summary>
    public SwarmTaskDetail? Detail
    {
        get => detail;
        private set => Set(ref detail, value);
    }

    /// <summary>
    /// Gets the title of the task.
    /// </summary>
    public string Title => task.Title;

    /// <summary>
    /// Gets where the task stands, with its capability.
    /// </summary>
    public string Status => $"{new SwarmTaskViewModel(task).Detail} - {task.Capability}";

    /// <summary>
    /// Gets what the last action did, or <see langword="null" />.
    /// </summary>
    public string? Outcome
    {
        get => outcome;
        private set
        {
            if (Set(ref outcome, value))
            {
                Raise(nameof(HasOutcome));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether there is something to tell about the last action.
    /// </summary>
    public bool HasOutcome => outcome is not null;

    /// <summary>
    /// Gets what the button that brings the result in says.
    /// </summary>
    public string BringInText => destination is null ? "Bring Into Main" : $"Bring Into {destination}";

    /// <summary>
    /// Gets what the switch of what happens once the task is done says.
    /// </summary>
    public string WhenDoneText => task.BringIn ? $"When Done: {BringInText}" : "When Done: Keep Separate";

    /// <summary>
    /// Gets a value indicating whether the result is brought in by itself once the task is done.
    /// </summary>
    public bool BringsIn => task.BringIn;

    /// <summary>
    /// Gets a value indicating whether the result can be brought in.
    /// </summary>
    public bool CanBringIn => task.CanBringIn;

    /// <summary>
    /// Gets a value indicating whether a side session can be made from the result.
    /// </summary>
    public bool CanContinue => task.CanBringIn;

    /// <summary>
    /// Gets a value indicating whether it can be chosen what happens once the task is done, which is while its result is still to come or to be brought in.
    /// </summary>
    public bool ShowWhenDone => !task.IsIncoming && (task.IsActive || task.State == SwarmTaskState.Done);

    /// <summary>
    /// Gets a value indicating whether the task can be cancelled.
    /// </summary>
    public bool CanCancel => task.CanCancel;

    /// <summary>
    /// Gets a value indicating whether the task can be delegated again.
    /// </summary>
    public bool CanRetry => task.CanRetry;

    /// <summary>
    /// Gets a value indicating whether the task can be removed.
    /// </summary>
    public bool CanDiscard => task.CanDiscard;

    /// <summary>
    /// Gets the command that has the result brought in.
    /// </summary>
    public AsyncCommand BringInCommand { get; }

    /// <summary>
    /// Gets the command that makes a side session from the result, to go on with it by hand.
    /// </summary>
    public AsyncCommand ContinueCommand { get; }

    /// <summary>
    /// Gets the command that switches bringing the result in once the task is done on or off.
    /// </summary>
    public AsyncCommand ToggleWhenDoneCommand { get; }

    /// <summary>
    /// Gets the command that cancels the task.
    /// </summary>
    public AsyncCommand CancelTaskCommand { get; }

    /// <summary>
    /// Gets the command that delegates the task again.
    /// </summary>
    public AsyncCommand RetryCommand { get; }

    /// <summary>
    /// Gets the command that removes the task.
    /// </summary>
    public AsyncCommand DiscardCommand { get; }

    /// <summary>
    /// Gets the command that closes the task.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Reads everything about the task; one that is gone closes.
    /// </summary>
    /// <returns>A task that completes once read.</returns>
    public async Task Load()
    {
        if (reading)
        {
            readAgain = true;
            return;
        }

        reading = true;

        try
        {
            do
            {
                readAgain = false;

                try
                {
                    SwarmTaskDetail found = await connection.GetSwarmTask(task.Id, CancellationToken.None);
                    Detail = found;
                    Take(found.Task);
                }
                catch (RemoteException exception)
                {
                    if (detail is null)
                    {
                        Error = exception.Message;
                    }
                    else
                    {
                        Close();
                    }
                }
                catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
                {
                    Error = exception.Message;
                }
            }
            while (readAgain && !closed);
        }
        finally
        {
            reading = false;
        }
    }

    /// <summary>
    /// Takes what the swarm says about the task now.
    /// </summary>
    /// <param name="current">The task.</param>
    public void Take(SwarmTaskInfo current)
    {
        if (current.Id == task.Id)
        {
            task = current;
            Raise(null);
        }
    }

    /// <summary>
    /// Stops following the task.
    /// </summary>
    public void Dispose()
    {
        closed = true;
        connection.EventReceived -= Receive;
        connection.Reconnected -= Resync;
    }

    private void Close()
    {
        if (!closed)
        {
            Dispose();
            Closed?.Invoke();
        }
    }

    private async Task<bool> Act(Func<Task<SwarmStatus>> action)
    {
        Outcome = null;
        SwarmStatus? status = null;

        if (!await Attempt(async () => status = await action()))
        {
            return false;
        }

        Outcome = status?.Report;

        if (status?.Tasks.FirstOrDefault(found => found.Id == task.Id) is { } current)
        {
            Take(current);
        }

        return true;
    }

    private async Task Continue()
    {
        WorkspaceInfo? made = null;

        if (await Attempt(async () => made = await connection.ContinueSwarmTask(task.Id, null, CancellationToken.None)))
        {
            Close();
            Continued?.Invoke(made!);
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind == RemoteEventKind.SwarmChanged || remoteEvent.Kind == RemoteEventKind.SwarmTaskChanged && remoteEvent.TaskId == task.Id)
        {
            _ = Load();
        }
    }

    private void Resync() => _ = Load();
}
