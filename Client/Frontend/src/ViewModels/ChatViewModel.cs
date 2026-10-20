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
    /// <param name="drafts">Where the text typed in the message box is kept, or <see langword="null" /> to keep none.</param>
    /// <param name="recorder">Records speech for voice input, or <see langword="null" /> when this client cannot record.</param>
    public ChatViewModel(IServerConnection connection, Guid workspaceId, IDraftStore? drafts = null, IVoiceRecorder? recorder = null)
    {
        this.recorder = recorder;
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.drafts = drafts;
        SendCommand = new AsyncCommand(_ => Send(), () => blockReason is null && (!string.IsNullOrWhiteSpace(input) || Attachments.Count > 0));
        RemoveAttachmentCommand = new AsyncCommand(parameter => Remove(parameter), null);
        DictateCommand = new AsyncCommand(_ => recording ? StopDictation() : StartDictation(), () => !transcribing && (recording || blockReason is null));
        Attachments.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasAttachments));
            SendCommand.Refresh();
        };
    }

    private readonly IServerConnection connection;

    private readonly IVoiceRecorder? recorder;

    private readonly TimeSpan longestRecording = TimeSpan.FromSeconds(60);

    private readonly TimeSpan shortestRecording = TimeSpan.FromMilliseconds(300);

    private CancellationTokenSource? recordingLimit;

    private bool recording;

    private bool transcribing;

    /// <summary>
    /// Raised when the user submits a prompt, before it is sent, so work this client gave can be told apart from work another client gave.
    /// </summary>
    public event Action? PromptStarted;

    /// <summary>
    /// Raised when a submitted prompt could not be sent, so no work started.
    /// </summary>
    public event Action? PromptFailed;

    private readonly Guid workspaceId;

    private readonly IDraftStore? drafts;

    private readonly TimeSpan draftDelay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? draftSaving;

    private readonly List<RemoteEvent> early = [];

    private readonly int chunk = 30;


    private int first;

    private string input = string.Empty;

    private bool isAiWorking;

    private string? blockReason;

    private bool loaded;

    private bool autoScroll = true;

    private bool subscribed;

    private bool loadingOlder;

    /// <summary>
    /// Raised once the stored conversation is shown, so the view can start at its end.
    /// </summary>
    public event Action? Loaded;

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
    /// Gets or sets why the AI cannot be given work now, such as a script or git running in the workspace, or <see langword="null" /> when it can; it is shown as the hint of the message box.
    /// </summary>
    public string? BlockReason
    {
        get => blockReason;
        set
        {
            if (Set(ref blockReason, value))
            {
                Raise(nameof(Placeholder));
                SendCommand.Refresh();
                DictateCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether this client can record speech, which shows the voice input button.
    /// </summary>
    public bool CanDictate => recorder is not null;

    /// <summary>
    /// Gets a value indicating whether speech is being recorded for the message box.
    /// </summary>
    public bool IsRecording => recording;

    /// <summary>
    /// Gets a value indicating whether a recording is being turned into text on the server.
    /// </summary>
    public bool IsTranscribing => transcribing;

    /// <summary>
    /// Gets the tooltip of the voice input button.
    /// </summary>
    public string DictateTip => recording ? "Stop recording and add the text" : transcribing ? "Turning speech into text" : "Voice input";

    /// <summary>
    /// Gets the command that starts recording speech, or ends the recording and adds what was said to <see cref="Input" />; it is unavailable while a recording is being turned into text.
    /// </summary>
    public AsyncCommand DictateCommand { get; }

    /// <summary>
    /// Gets the hint shown in the empty message box.
    /// </summary>
    public string Placeholder => blockReason ?? "Enter query...";

    /// <summary>
    /// Gets a value indicating whether older messages exist on the server than the ones loaded so far.
    /// </summary>
    public bool HasMore => first > 0;

    /// <summary>
    /// Gets or sets the prompt being typed, which is kept on the device and comes back when the workspace is opened again.
    /// </summary>
    public string Input
    {
        get => input;
        set
        {
            if (Set(ref input, value))
            {
                SendCommand.Refresh();
                SaveDraft();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the conversation scrolls to the bottom whenever it gets new content.
    /// </summary>
    public bool AutoScroll
    {
        get => autoScroll;
        set => Set(ref autoScroll, value);
    }

    /// <summary>
    /// Gets the largest file, in bytes, that the server accepts as an attachment.
    /// </summary>
    public int MaxAttachment { get; } = 2 * 1024 * 1024;

    /// <summary>
    /// Gets the files waiting to be sent with the next prompt.
    /// </summary>
    public ObservableCollection<ChatAttachment> Attachments { get; } = [];

    /// <summary>
    /// Gets a value indicating whether any file waits to be sent.
    /// </summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Gets the command that removes the <see cref="ChatAttachment" /> given as its parameter from <see cref="Attachments" />.
    /// </summary>
    public AsyncCommand RemoveAttachmentCommand { get; }

    /// <summary>
    /// Gets the command that sends <see cref="Input" /> to the AI; it stays available while the AI works, and the prompt then joins that work, but not while a script or git runs.
    /// </summary>
    public AsyncCommand SendCommand { get; }

    /// <summary>
    /// Adds a file to send with the next prompt, unless it is empty or larger than the server accepts.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="data">The file content.</param>
    public void Attach(string name, ReadOnlyMemory<byte> data)
    {
        if (data.Length == 0 || data.Length > MaxAttachment)
        {
            Error = $"{name} is empty or larger than {MaxAttachment / 1024 / 1024} MB.";
            return;
        }

        Error = null;
        Attachments.Add(new() { Name = name, Data = data });
    }

    /// <summary>
    /// Reads the newest messages again after the connection was lost and reopened, since messages and whether the AI works may have changed while no events arrived.
    /// </summary>
    /// <returns>A task that completes once the conversation is up to date.</returns>
    public Task Resync() => loaded ? Load() : Task.CompletedTask;

    /// <summary>
    /// Shows a failure that happened outside the conversation, such as a refused stop, where the conversation shows its own.
    /// </summary>
    /// <param name="message">The failure.</param>
    public void Show(string message) => Error = message;

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

        await Restore();
        await Load();
        loaded = true;

        foreach (RemoteEvent remoteEvent in early)
        {
            Apply(remoteEvent);
        }

        early.Clear();
        Loaded?.Invoke();
    }

    /// <summary>
    /// Stops following the conversation.
    /// </summary>
    public void Dispose()
    {
        if (recording)
        {
            recording = false;
            recordingLimit?.Cancel();
            _ = recorder!.Stop();
        }

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

    private Task Remove(object? attachment)
    {
        if (attachment is ChatAttachment found)
        {
            Attachments.Remove(found);
        }

        return Task.CompletedTask;
    }

    private async Task Restore()
    {
        if (drafts is null || input.Length > 0)
        {
            return;
        }

        try
        {
            string saved = await drafts.Load(workspaceId, CancellationToken.None);

            if (input.Length == 0)
            {
                Input = saved;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SaveDraft()
    {
        if (drafts is null)
        {
            return;
        }

        draftSaving?.Cancel();
        CancellationTokenSource source = new();
        draftSaving = source;
        _ = Persist(input, source.Token);
    }

    private async Task Persist(string text, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(draftDelay, cancellation);
            await drafts!.Save(workspaceId, text, CancellationToken.None);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private void SetDictation(bool isRecording, bool isTranscribing)
    {
        recording = isRecording;
        transcribing = isTranscribing;
        Raise(nameof(IsRecording));
        Raise(nameof(IsTranscribing));
        Raise(nameof(DictateTip));
        DictateCommand.Refresh();
    }

    private async Task StartDictation()
    {
        Error = null;

        try
        {
            await recorder!.Start(CancellationToken.None);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = exception.Message;
            return;
        }

        SetDictation(true, false);
        CancellationTokenSource limit = new();
        recordingLimit = limit;
        _ = StopWhenTooLong(limit.Token);
    }

    private async Task StopWhenTooLong(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(longestRecording, cancellation);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await StopDictation();
    }

    private async Task StopDictation()
    {
        recordingLimit?.Cancel();
        recordingLimit = null;
        SetDictation(false, true);

        try
        {
            ReadOnlyMemory<byte> pcm = await recorder!.Stop();

            if (pcm.Length < recorder.SampleRate * 2 * shortestRecording.TotalSeconds)
            {
                Error = "Nothing was recorded.";
                return;
            }

            string text = string.Empty;

            if (await Attempt(async () => text = await connection.Transcribe(Wav(pcm, recorder.SampleRate), CancellationToken.None)))
            {
                Dictated(text.Trim());
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = exception.Message;
        }
        finally
        {
            SetDictation(false, false);
        }
    }

    private void Dictated(string text)
    {
        if (text.Length == 0)
        {
            Error = "Nothing was heard.";
            return;
        }

        Input = input.Length == 0 || char.IsWhiteSpace(input[^1]) ? input + text : $"{input} {text}";
    }

    private ReadOnlyMemory<byte> Wav(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        byte[] wav = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.TryWriteBytes(wav.AsSpan(4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.TryWriteBytes(wav.AsSpan(16), 16);
        BitConverter.TryWriteBytes(wav.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(wav.AsSpan(24), sampleRate);
        BitConverter.TryWriteBytes(wav.AsSpan(28), sampleRate * 2);
        BitConverter.TryWriteBytes(wav.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(wav.AsSpan(34), (short)16);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.TryWriteBytes(wav.AsSpan(40), pcm.Length);
        pcm.CopyTo(wav.AsMemory(44));
        return wav;
    }

    private async Task Send()
    {
        string text = input.Trim();
        List<ChatAttachment> sending = [.. Attachments];
        Input = string.Empty;
        Attachments.Clear();

        PromptStarted?.Invoke();

        bool sent = await Attempt(async () =>
        {
            List<string> lines = text.Length == 0 ? [] : [text];

            foreach (ChatAttachment attachment in sending)
            {
                lines.Add($"Attached file: {await connection.UploadAttachment(workspaceId, attachment.Name, attachment.Data, CancellationToken.None)}");
            }

            await connection.Prompt(workspaceId, string.Join('\n', lines), CancellationToken.None);
        });

        if (!sent)
        {
            PromptFailed?.Invoke();
        }

        if (!sent && input.Length == 0 && Attachments.Count == 0)
        {
            Input = text;

            foreach (ChatAttachment attachment in sending)
            {
                Attachments.Add(attachment);
            }
        }
    }
}
