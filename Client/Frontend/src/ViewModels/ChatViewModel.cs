namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The live conversation with the AI in one workspace: messages arrive as the AI writes them, and more can be sent while it works.
/// </summary>
internal sealed class ChatViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChatViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace whose conversation this is.</param>
    public ChatViewModel(IServerConnection connection, Guid workspaceId)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        SendCommand = new AsyncCommand(_ => Send(), () => !string.IsNullOrWhiteSpace(input));
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly List<RemoteEvent> early = [];

    private readonly int chunk = 30;

    private int first;

    private string input = string.Empty;

    private bool isAiWorking;

    private bool loaded;

    private bool subscribed;

    private bool loadingOlder;

    /// <summary>
    /// Gets the messages in order.
    /// </summary>
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace, whichever client gave it the work.
    /// </summary>
    public bool IsAiWorking
    {
        get => isAiWorking;
        private set => Set(ref isAiWorking, value);
    }

    /// <summary>
    /// Gets a value indicating whether older messages exist on the server than the ones loaded so far.
    /// </summary>
    public bool HasMore => first > 0;

    /// <summary>
    /// Gets or sets the prompt being typed.
    /// </summary>
    public string Input
    {
        get => input;
        set
        {
            if (Set(ref input, value))
            {
                SendCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets the command that sends <see cref="Input" /> to the AI; it stays available while the AI works, and the prompt then joins that work.
    /// </summary>
    public AsyncCommand SendCommand { get; }

    /// <summary>
    /// Loads the newest chunk of the stored conversation and starts following it live.
    /// </summary>
    /// <returns>A task that completes once the conversation is shown.</returns>
    public async Task Start()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
        }

        await Load();
        loaded = true;

        foreach (RemoteEvent remoteEvent in early)
        {
            Apply(remoteEvent);
        }

        early.Clear();
    }

    /// <summary>
    /// Stops following the conversation.
    /// </summary>
    public void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
        }
    }

    /// <summary>
    /// Loads the chunk of messages before the oldest one shown.
    /// </summary>
    /// <returns><see langword="true" /> if older messages were added at the top.</returns>
    public async Task<bool> LoadOlder()
    {
        if (!HasMore || loadingOlder)
        {
            return false;
        }

        loadingOlder = true;
        ChatHistory? history = null;

        try
        {
            if (!await Attempt(async () => history = await connection.GetHistory(workspaceId, first, chunk, CancellationToken.None)) || history!.Messages.Count == 0)
            {
                return false;
            }

            for (int index = 0; index < history.Messages.Count; index++)
            {
                Messages.Insert(index, history.Messages[index]);
            }

            first = history.Start;
            Raise(nameof(HasMore));
            return true;
        }
        finally
        {
            loadingOlder = false;
        }
    }

    private async Task Load()
    {
        ChatHistory? history = null;

        if (await Attempt(async () => history = await connection.GetHistory(workspaceId, null, chunk, CancellationToken.None)))
        {
            Messages.Clear();

            foreach (ChatMessage message in history!.Messages)
            {
                Messages.Add(message);
            }

            first = history.Start;
            Raise(nameof(HasMore));
            IsAiWorking = history.Working;
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId != workspaceId)
        {
            return;
        }

        if (loaded)
        {
            Apply(remoteEvent);
        }
        else
        {
            early.Add(remoteEvent);
        }
    }

    private void Apply(RemoteEvent remoteEvent)
    {
        switch (remoteEvent.Kind)
        {
            case RemoteEventKind.MessageAdded when remoteEvent.Message is { } message && remoteEvent.Index is { } index:
                if (index == first + Messages.Count)
                {
                    Messages.Add(message);
                }
                else if (index > first + Messages.Count)
                {
                    _ = Load();
                }

                break;
            case RemoteEventKind.WorkingChanged:
                IsAiWorking = remoteEvent.Working == true;
                break;
            case RemoteEventKind.Failed:
                Error = remoteEvent.Error;
                break;
        }
    }

    private async Task Send()
    {
        string text = input.Trim();
        Input = string.Empty;

        if (!await Attempt(() => connection.Prompt(workspaceId, text, CancellationToken.None)) && input.Length == 0)
        {
            Input = text;
        }
    }
}
