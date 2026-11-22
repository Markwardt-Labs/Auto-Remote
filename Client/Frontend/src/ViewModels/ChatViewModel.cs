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
        SendCommand = new AsyncCommand(_ => Send(), () => blockReason is null && HasContent);
        ReviewCommand = new AsyncCommand(_ => Preset(settings => settings.ReviewPrompt ?? settings.GlobalReviewPrompt), () => blockReason is null && !HasContent);
        ExpandCommand = new AsyncCommand(_ => Preset(settings => settings.ExpandPrompt ?? settings.GlobalExpandPrompt), () => blockReason is null && !HasContent);
        PauseCommand = new AsyncCommand(_ => Deliver(PausePrompt, []), () => blockReason is null && !HasContent);
        ResumeCommand = new AsyncCommand(_ => Deliver(ResumePrompt, []), () => blockReason is null && !HasContent);
        ConfirmOverLimitCommand = new AsyncCommand(_ => ConfirmOverLimit());
        CancelOverLimitCommand = new AsyncCommand(_ =>
        {
            ClearOverLimit();
            return Task.CompletedTask;
        });
        CancelLimitPauseCommand = new AsyncCommand(_ => CancelLimitPause());
        RemoveAttachmentCommand = new AsyncCommand(parameter => Remove(parameter), null);
        SaveInputCommand = new AsyncCommand(_ => SaveInput(), () => HasText);
        ToggleSavedInputsCommand = new AsyncCommand(_ =>
        {
            IsSavedInputsOpen = !IsSavedInputsOpen;
            return Task.CompletedTask;
        });
        LoadSavedInputCommand = new AsyncCommand(parameter => LoadSavedInput(parameter));
        DictateCommand = new AsyncCommand(_ => recording ? StopDictation() : StartDictation(), () => !transcribing && (recording || blockReason is null));
        Attachments.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasAttachments));
            Raise(nameof(HasContent));
            SendCommand.Refresh();
            RefreshQuick();
        };
    }

    private readonly IServerConnection connection;

    private string? notice;

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

    private readonly int chunk = 20;

    private int start;

    private bool loading;

    private string input = string.Empty;

    private bool isAiWorking;

    private string? blockReason;

    private bool loaded;

    private bool autoScroll = true;

    private bool subscribed;

    private bool loadingOlder;

    private bool showWork;

    private LimitPause? limitPause;

    private OverLimitAsk? overLimit;

    private ChatMessage? streaming;

    private bool isSavedInputsOpen;

    /// <summary>
    /// Raised once the stored conversation is shown, so the view can start at its end.
    /// </summary>
    public event Action? Loaded;

    /// <summary>
    /// Gets the messages in order: a window of the newest ones, with the chunks the user loaded by scrolling up in front of them until the conversation is back at its end.
    /// </summary>
    public ChatMessageList Messages { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace, whichever client gave it the work.
    /// </summary>
    public bool IsAiWorking
    {
        get => isAiWorking;
        private set => Set(ref isAiWorking, value);
    }

    /// <summary>
    /// Gets or sets why the AI cannot be given work now, such as git running in the workspace, or <see langword="null" /> when it can; it is shown as the hint of the message box.
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
                RefreshQuick();
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
    public bool HasMore => start > 0;

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
                Raise(nameof(HasContent));
                Raise(nameof(HasText));
                SaveInputCommand.Refresh();

                if (HasText)
                {
                    IsSavedInputsOpen = false;
                }

                SendCommand.Refresh();
                RefreshQuick();
                SaveDraft();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether anything but white space is typed in the message box, which offers saving it; without text the message box offers loading a saved input instead.
    /// </summary>
    public bool HasText => !string.IsNullOrWhiteSpace(input);

    /// <summary>
    /// Gets the chat inputs saved in the workspace, the last saved first, which every client of the server sees.
    /// </summary>
    public ObservableCollection<SavedInput> SavedInputs { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the list of saved inputs is shown.
    /// </summary>
    public bool IsSavedInputsOpen
    {
        get => isSavedInputsOpen;
        private set => Set(ref isSavedInputsOpen, value);
    }

    /// <summary>
    /// Gets a value indicating whether no input is saved, which the list says in place of its entries.
    /// </summary>
    public bool HasNoSavedInputs => SavedInputs.Count == 0;

    /// <summary>
    /// Gets the automatic pause the AI of the workspace is in because a usage window of its provider reached the maximum of the global settings, or <see langword="null" /> when it is in none.
    /// </summary>
    public LimitPause? LimitPause
    {
        get => limitPause;
        private set
        {
            if (Set(ref limitPause, value))
            {
                Raise(nameof(HasLimitPause));
                Raise(nameof(LimitPauseText));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the server refused the input because a usage window is at or past the maximum, so the user is asked whether to start the work anyway.
    /// </summary>
    public bool IsAskingOverLimit => overLimit is not null;

    /// <summary>
    /// Gets what the question says: which window is at what share of its limit and that the maximum is ignored for the work the input starts.
    /// </summary>
    public string OverLimitText => overLimit is { } ask ? $"The {ask.Pause.Window} usage window is at {ask.Pause.PercentUsed:0.#}%, at or past the maximum of {ask.Pause.MaxPercent:0.#}%. Start the work anyway? The workspace ignores the maximum until this work is done." : string.Empty;

    /// <summary>
    /// Gets the command that sends the refused input anyway, which makes the workspace ignore the maximum until the work it starts is done.
    /// </summary>
    public AsyncCommand ConfirmOverLimitCommand { get; }

    /// <summary>
    /// Gets the command that drops the question and sends nothing.
    /// </summary>
    public AsyncCommand CancelOverLimitCommand { get; }

    /// <summary>
    /// Gets the command that ends the automatic pause without resuming anything and ends the loop, so nothing goes on by itself.
    /// </summary>
    public AsyncCommand CancelLimitPauseCommand { get; }

    /// <summary>
    /// Gets a value indicating whether the AI of the workspace is paused automatically because of a usage window, which the chat and the header show.
    /// </summary>
    public bool HasLimitPause => limitPause is not null;

    /// <summary>
    /// Gets what the automatic pause says: which window is at what share of its limit, what the maximum is and when the server looks again.
    /// </summary>
    public string LimitPauseText => limitPause is { } pause ? $"Paused automatically: the {pause.Window} usage window is at {pause.PercentUsed:0.#}%, at or past the maximum of {pause.MaxPercent:0.#}%. The AI resumes once it is below the maximum; the server checks again at {pause.NextCheckAt.ToLocalTime():t}." : string.Empty;

    /// <summary>
    /// Gets a value indicating whether the server holds at least one saved input of the workspace, which the load button shows with a blue border.
    /// </summary>
    public bool HasSavedInputs => SavedInputs.Count > 0;

    /// <summary>
    /// Gets the command that saves <see cref="Input" /> as a saved input on the server and clears it; it is available while text is typed.
    /// </summary>
    public AsyncCommand SaveInputCommand { get; }

    /// <summary>
    /// Gets the command that shows or hides the list of saved inputs.
    /// </summary>
    public AsyncCommand ToggleSavedInputsCommand { get; }

    /// <summary>
    /// Gets the command that takes the <see cref="SavedInput" /> given as its parameter off the server's list and puts its text into <see cref="Input" />.
    /// </summary>
    public AsyncCommand LoadSavedInputCommand { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the conversation scrolls to the bottom whenever it gets new content.
    /// </summary>
    public bool AutoScroll
    {
        get => autoScroll;
        set
        {
            if (Set(ref autoScroll, value) && value)
            {
                TrimToNewest();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the conversation also shows the work the AI does on the way, its tool calls and their results, dimmed. The server keeps it for the workspace and tells every client, so setting it changes it for all of them.
    /// </summary>
    public bool ShowWork
    {
        get => showWork;
        set
        {
            if (showWork != value)
            {
                showWork = value;
                Raise(nameof(ShowWork));
                _ = Store(value);
            }
        }
    }

    /// <summary>
    /// Gets the largest file, in bytes, that the server accepts as an attachment.
    /// </summary>
    public int MaxAttachment { get; } = 20 * 1024 * 1024;

    /// <summary>
    /// Gets the prompt the pause command sends.
    /// </summary>
    public string PausePrompt { get; } = "pause current work";

    /// <summary>
    /// Gets the prompt the resume command sends.
    /// </summary>
    public string ResumePrompt { get; } = "resume unfinished work";

    /// <summary>
    /// Gets a value indicating whether there is anything to send: text in the message box or a file waiting to go with it. Without it the message box offers the review button instead of the send button.
    /// </summary>
    public bool HasContent => !string.IsNullOrWhiteSpace(input) || Attachments.Count > 0;

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
    /// Gets the command that sends <see cref="Input" /> to the AI; it stays available while the AI works, and the prompt then joins that work, but not while git runs.
    /// </summary>
    public AsyncCommand SendCommand { get; }

    /// <summary>
    /// Gets the command that asks the AI to review the workspace by sending the review prompt of the workspace, or the global one when it has none; it is available while nothing is typed, in the same states as <see cref="SendCommand" />.
    /// </summary>
    public AsyncCommand ReviewCommand { get; }

    /// <summary>
    /// Gets the command that asks the AI to expand upon the content of the workspace by sending the expand prompt of the workspace, or the global one when it has none; it is available in the same states as <see cref="ReviewCommand" />.
    /// </summary>
    public AsyncCommand ExpandCommand { get; }

    /// <summary>
    /// Gets the command that asks the AI to pause its current work by sending <see cref="PausePrompt" />; it is available in the same states as <see cref="ReviewCommand" />.
    /// </summary>
    public AsyncCommand PauseCommand { get; }

    /// <summary>
    /// Gets the command that asks the AI to pick up the work it left unfinished by sending <see cref="ResumePrompt" />; it is available in the same states as <see cref="ReviewCommand" />.
    /// </summary>
    public AsyncCommand ResumeCommand { get; }

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
    public Task Resync() => loaded ? Reload() : Task.CompletedTask;

    /// <summary>
    /// Gets or sets a note under the conversation that is not a failure, such as why a loop ended by itself; it goes away when the next prompt is sent.
    /// </summary>
    public string? Notice
    {
        get => notice;
        set
        {
            if (Set(ref notice, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="Notice" /> is set.
    /// </summary>
    public bool HasNotice => notice is not null;

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

        loading = true;
        await Restore();
        await Load();
        loaded = true;
        Flush();
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
    /// <returns><see langword="true" /> if older messages were added at the top; <see langword="false" /> when there were none or the window moved while they were read (messages were dropped or the conversation was read again), in which case they no longer fit in front of it.</returns>
    public async Task<bool> LoadOlder()
    {
        if (!HasMore || loadingOlder)
        {
            return false;
        }

        loadingOlder = true;
        ChatHistory? history = null;
        int requested = start;

        try
        {
            if (!await Attempt(async () => history = await connection.GetHistory(workspaceId, requested.ToString(CultureInfo.InvariantCulture), chunk, CancellationToken.None)))
            {
                return false;
            }

            if (start != requested)
            {
                return false;
            }

            start = Position(history!.Older);
            Raise(nameof(HasMore));

            if (history.Messages.Count == 0)
            {
                return false;
            }

            Messages.InsertFirst(history.Messages);
            return true;
        }
        finally
        {
            loadingOlder = false;
        }
    }

    /// <summary>
    /// Drops the oldest messages beyond the newest chunk, which are the chunks the user loaded by scrolling up and, while the conversation follows its end, the ones that fell out of the window, so a long conversation never makes the view build more than a few chunks. What is dropped can be loaded again by scrolling to the top.
    /// </summary>
    /// <returns><see langword="true" /> if messages were dropped.</returns>
    public bool TrimToNewest()
    {
        int drop = Messages.Count - chunk;

        if (drop <= 0)
        {
            return false;
        }

        bool hadMore = HasMore;
        Messages.RemoveFirst(drop);
        start += drop;

        if (!hadMore)
        {
            Raise(nameof(HasMore));
        }

        return true;
    }

    private int Position(string? cursor) => cursor is not null && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int position) ? position : 0;

    private void KeepWindow()
    {
        if (autoScroll)
        {
            TrimToNewest();
        }
    }

    private async Task Reload()
    {
        loading = true;

        try
        {
            await Load();
        }
        finally
        {
            Flush();
        }
    }

    private void Stream(string piece)
    {
        if (streaming is null)
        {
            streaming = new ChatMessage { Role = MessageRole.Assistant, Text = piece };
            Messages.Add(streaming);
            KeepWindow();
            return;
        }

        ChatMessage updated = streaming with { Text = streaming.Text + piece };
        Replace(streaming, updated);
        streaming = updated;
    }

    private void Unstream()
    {
        if (streaming is not null)
        {
            int index = IndexOf(streaming);

            if (index >= 0)
            {
                Messages.RemoveAt(index);
            }

            streaming = null;
        }
    }

    private void Replace(ChatMessage old, ChatMessage replacement)
    {
        int index = IndexOf(old);

        if (index >= 0)
        {
            Messages[index] = replacement;
        }
        else
        {
            Messages.Add(replacement);
            KeepWindow();
        }
    }

    private int IndexOf(ChatMessage message)
    {
        for (int index = Messages.Count - 1; index >= 0; index--)
        {
            if (ReferenceEquals(Messages[index], message))
            {
                return index;
            }
        }

        return -1;
    }

    private bool IsHidden(RemoteEvent remoteEvent) => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message is { IsWork: true } && !showWork;

    private async Task Store(bool value)
    {
        if (!await Attempt(() => connection.SetShowWork(workspaceId, value, CancellationToken.None)))
        {
            showWork = !value;
            Raise(nameof(ShowWork));
        }
    }

    private void Flush()
    {
        List<RemoteEvent> pending = [.. early];
        early.Clear();
        List<ChatMessage> added = [.. pending.Where(item => item.Kind == RemoteEventKind.MessageAdded && item.Message is not null && !IsHidden(item)).Select(item => item.Message!)];
        int overlap = Math.Min(added.Count, Messages.Count);

        while (overlap > 0 && !Enumerable.Range(0, overlap).All(index => Same(Messages[Messages.Count - overlap + index], added[index])))
        {
            overlap--;
        }

        int skipped = 0;
        loading = false;

        foreach (RemoteEvent remoteEvent in pending)
        {
            if (remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message is not null && !IsHidden(remoteEvent) && skipped < overlap)
            {
                skipped++;
            }
            else
            {
                Apply(remoteEvent);
            }
        }
    }

    private bool Same(ChatMessage first, ChatMessage second) => first.Role == second.Role && first.Text.Trim() == second.Text.Trim();

    private async Task Load()
    {
        ChatHistory? history = null;
        IReadOnlyList<SavedInput>? saved = null;

        if (await Attempt(async () => saved = await connection.GetSavedInputs(workspaceId, CancellationToken.None)))
        {
            ShowSavedInputs(saved!);
        }

        if (await Attempt(async () => history = await connection.GetHistory(workspaceId, null, chunk, CancellationToken.None)))
        {
            streaming = null;
            Messages.Reset(history!.Messages);
            start = Position(history.Older);
            Raise(nameof(HasMore));
            IsAiWorking = history.Working;
            LimitPause = history.LimitPause;

            if (showWork != history.ShowWork)
            {
                showWork = history.ShowWork;
                Raise(nameof(ShowWork));
            }
        }
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId != workspaceId)
        {
            return;
        }

        if (loading)
        {
            early.Add(remoteEvent);
        }
        else
        {
            Apply(remoteEvent);
        }
    }

    private void Apply(RemoteEvent remoteEvent)
    {
        switch (remoteEvent.Kind)
        {
            case RemoteEventKind.MessageAdded when remoteEvent.Message is { } message:
                if (IsHidden(remoteEvent))
                {
                    break;
                }

                if (message.Role == MessageRole.User)
                {
                    Error = null;
                }

                if (streaming is not null && message is { Role: MessageRole.Assistant, IsWork: false })
                {
                    Replace(streaming, message);
                    streaming = null;
                }
                else
                {
                    Messages.Add(message);
                    KeepWindow();
                }

                break;
            case RemoteEventKind.MessageDelta when showWork && remoteEvent.Text is { Length: > 0 } piece:
                Stream(piece);
                break;
            case RemoteEventKind.ShowWorkChanged:
                _ = Reload();
                break;
            case RemoteEventKind.WorkingChanged:
                IsAiWorking = remoteEvent.Working == true;

                if (!IsAiWorking)
                {
                    Unstream();
                }

                break;
            case RemoteEventKind.Failed:
                Report(remoteEvent.Error ?? "The AI failed.", remoteEvent.Time ?? DateTimeOffset.UtcNow);
                Unstream();
                break;
            case RemoteEventKind.LimitPauseChanged:
                LimitPause = remoteEvent.LimitPause;
                break;
            case RemoteEventKind.SavedInputsChanged when remoteEvent.SavedInputs is { } saved:
                ShowSavedInputs(saved);
                break;
        }
    }

    private void ShowSavedInputs(IReadOnlyList<SavedInput> saved)
    {
        SavedInputs.Clear();

        foreach (SavedInput entry in saved)
        {
            SavedInputs.Add(entry);
        }

        Raise(nameof(HasNoSavedInputs));
        Raise(nameof(HasSavedInputs));
    }

    private async Task SaveInput()
    {
        string text = input;
        IReadOnlyList<SavedInput>? saved = null;

        if (!await Attempt(async () => saved = await connection.SaveInput(workspaceId, text, CancellationToken.None)))
        {
            return;
        }

        ShowSavedInputs(saved!);

        if (input == text)
        {
            Input = string.Empty;
        }
    }

    private async Task LoadSavedInput(object? parameter)
    {
        if (parameter is not SavedInput chosen)
        {
            return;
        }

        SavedInput? taken = null;
        bool worked = await Attempt(async () => taken = await connection.TakeSavedInput(workspaceId, chosen.Id, CancellationToken.None));
        IsSavedInputsOpen = false;

        if (worked)
        {
            Input = HasText ? taken!.Text + "\n" + input : taken!.Text;
        }
        else
        {
            IReadOnlyList<SavedInput>? saved = null;
            string? failure = Error;

            if (await Attempt(async () => saved = await connection.GetSavedInputs(workspaceId, CancellationToken.None)))
            {
                ShowSavedInputs(saved!);
                Error = failure;
            }
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

        if (!await Deliver(text, sending) && input.Length == 0 && Attachments.Count == 0)
        {
            Input = text;

            foreach (ChatAttachment attachment in sending)
            {
                Attachments.Add(attachment);
            }
        }
    }

    private void RefreshQuick()
    {
        ReviewCommand.Refresh();
        ExpandCommand.Refresh();
        PauseCommand.Refresh();
        ResumeCommand.Refresh();
    }

    private async Task Preset(Func<WorkspaceSettings, string> choose)
    {
        string text = string.Empty;

        if (await Attempt(async () => text = choose(await connection.GetWorkspaceSettings(workspaceId, CancellationToken.None)).Trim()))
        {
            if (text.Length == 0)
            {
                Error = "The prompt of this button is empty.";
                return;
            }

            await Deliver(text, []);
        }
    }

    private async Task<bool> Deliver(string text, List<ChatAttachment> sending)
    {
        Notice = null;
        PromptStarted?.Invoke();

        OverLimitAsk? refused = null;

        bool sent = await Attempt(async () =>
        {
            List<string> lines = text.Length == 0 ? [] : [text];

            foreach (ChatAttachment attachment in sending)
            {
                lines.Add($"Attached file: {await connection.UploadAttachment(workspaceId, attachment.Name, attachment.Data, CancellationToken.None)}");
            }

            string prompt = string.Join('\n', lines);

            try
            {
                await connection.Prompt(workspaceId, prompt, CancellationToken.None);
            }
            catch (RemoteException exception) when (exception.OverLimit is { } pause)
            {
                refused = new OverLimitAsk(pause, prompt, text, sending.Count);
            }
        });

        if (refused is not null)
        {
            overLimit = refused;
            Raise(nameof(IsAskingOverLimit));
            Raise(nameof(OverLimitText));
            PromptFailed?.Invoke();
            return false;
        }

        if (!sent)
        {
            PromptFailed?.Invoke();
        }

        return sent;
    }

    private void ClearOverLimit()
    {
        overLimit = null;
        Raise(nameof(IsAskingOverLimit));
        Raise(nameof(OverLimitText));
    }

    private async Task ConfirmOverLimit()
    {
        if (overLimit is not { } ask)
        {
            return;
        }

        ClearOverLimit();

        if (ask.Typed.Length > 0 && input.Trim() == ask.Typed && Attachments.Count == ask.Attachments)
        {
            Input = string.Empty;
            Attachments.Clear();
        }

        PromptStarted?.Invoke();

        if (!await Attempt(() => connection.PromptOverLimit(workspaceId, ask.Prompt, CancellationToken.None)))
        {
            PromptFailed?.Invoke();
        }
    }

    private async Task CancelLimitPause()
    {
        if (await Attempt(() => connection.CancelLimitPause(workspaceId, CancellationToken.None)))
        {
            Notice = "The automatic resume was cancelled, so nothing goes on by itself. Send a message to start work again.";
        }
    }

    private sealed record OverLimitAsk(LimitPause Pause, string Prompt, string Typed, int Attachments);
}
