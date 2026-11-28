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
        PauseCommand = new AsyncCommand(_ => Deliver(PausePrompt, [], false), () => blockReason is null && !HasContent);
        ResumeCommand = new AsyncCommand(_ => Deliver(ResumePrompt, [], false), () => blockReason is null && !HasContent);
        ConfirmOverLimitCommand = new AsyncCommand(_ => ConfirmOverLimit());
        CancelOverLimitCommand = new AsyncCommand(_ =>
        {
            ClearOverLimit();
            return Task.CompletedTask;
        });
        CancelLimitPauseCommand = new AsyncCommand(_ => CancelLimitPause());
        CancelQueuedCommand = new AsyncCommand(_ => CancelQueued());
        RemoveAttachmentCommand = new AsyncCommand(parameter => Remove(parameter), null);
        SaveInputCommand = new AsyncCommand(_ => SaveInput(), () => HasText);
        ToggleSavedInputsCommand = new AsyncCommand(_ =>
        {
            IsSavedInputsOpen = !IsSavedInputsOpen;
            return Task.CompletedTask;
        }, () => !isDelegated);
        ToggleUpNextCommand = new AsyncCommand(_ =>
        {
            IsUpNextOpen = !IsUpNextOpen;
            return Task.CompletedTask;
        });
        EditQueuedCommand = new AsyncCommand(parameter => LoadSavedInput(QueuedInputOf(parameter)), () => !isDelegated, false);
        ToggleAgendaCommitCommand = new AsyncCommand(ToggleAgendaCommit, () => !isDelegated, false);
        AskDeleteQueuedCommand = new AsyncCommand(parameter =>
        {
            DeletingInput = QueuedInputOf(parameter);
            return Task.CompletedTask;
        }, () => !isDelegated, false);
        LoadSavedInputCommand = new AsyncCommand(parameter => LoadSavedInput(parameter), null, false);
        // What is typed can be queued whatever the workspace is doing, a commit included.
        QueueCommand = new AsyncCommand(_ => QueueTyped(loadedAutoCommit ?? true), () => !isDelegated && HasContent);
        ToggleAutoCommitCommand = new AsyncCommand(_ => ToggleAutoCommit());
        ResumeAutomationCommand = new AsyncCommand(_ => ResumeAutomation(), () => !isDelegated);
        StopRetryingCommand = new AsyncCommand(_ => Attempt(() => connection.StopWorkspace(workspaceId, CancellationToken.None)), () => !isDelegated);
        ToggleSavedInputAutoCommitCommand = new AsyncCommand(parameter => ToggleSavedInputAutoCommit(parameter), null, false);
        AskDeleteSavedInputCommand = new AsyncCommand(parameter =>
        {
            DeletingInput = parameter as SavedInput;
            return Task.CompletedTask;
        });
        ConfirmDeleteSavedInputCommand = new AsyncCommand(_ =>
        {
            SavedInput? deleted = deletingInput;
            DeletingInput = null;
            return DiscardSavedInput(deleted);
        }, null, false);
        CancelDeleteSavedInputCommand = new AsyncCommand(_ =>
        {
            DeletingInput = null;
            return Task.CompletedTask;
        });
        DictateCommand = new AsyncCommand(_ => StartDictation(), () => !recording && !sending && blockReason is null);
        CompleteDictationCommand = new AsyncCommand(_ => CompleteDictation(), () => recording);
        CancelDictationCommand = new AsyncCommand(_ => CancelDictation(), () => recording);
        DiscardSavedInputCommand = new AsyncCommand(parameter => DiscardSavedInput(parameter), null, false);
        Attachments.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasAttachments));
            Raise(nameof(HasContent));
            ForgetEmptied();
            SendCommand.Refresh();
            QueueCommand.Refresh();
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

    private bool sending;

    private bool takingSavedInput;

    private readonly HashSet<Guid> discardingSavedInputs = [];

    private DateTime recordingStarted;

    private readonly TimeSpan recordingTick = TimeSpan.FromSeconds(1);

    private readonly Guid workspaceId;

    private readonly IDraftStore? drafts;

    private readonly TimeSpan draftDelay = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? draftSaving;

    private readonly List<RemoteEvent> early = [];

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

    private QueueStatus? queue;

    private readonly List<MessageParts> submitted = [];

    private bool isDelegated;

    private OverLimitAsk? overLimit;

    private ChatMessage? streaming;

    private bool isSavedInputsOpen;

    private bool isUpNextOpen;

    private Agenda agenda = new();

    private SavedInput? deletingInput;

    private AutoCommitState autoCommit;

    private bool? loadedAutoCommit;

    /// <summary>
    /// Raised when the user submits a prompt, before it is sent, so work this client gave can be told apart from work another client gave.
    /// </summary>
    public event Action? PromptStarted;

    /// <summary>
    /// Raised when a submitted prompt could not be sent, so no work started.
    /// </summary>
    public event Action? PromptFailed;

    /// <summary>
    /// Raised when work that waited for the local AI will never start, right before <see cref="PromptFailed" /> is raised for it. No submission led to it, so there is no earlier wait to go back to.
    /// </summary>
    public event Action? QueuedWorkDropped;

    /// <summary>
    /// Raised once the stored conversation is shown, so the view can start at its end.
    /// </summary>
    public event Action? Loaded;

    /// <summary>
    /// Raised when new output was added at the end of the conversation: a message or a piece of one as it is written, also what turned out to be new when the conversation was read again. Older messages loaded in front are not output.
    /// </summary>
    public event Action? OutputArrived;

    /// <summary>
    /// Gets the most messages a chunk of the conversation holds, which is both what a read asks the server for and what the window of the newest messages keeps.
    /// </summary>
    internal int ChunkMessages { get; init; } = 100;

    /// <summary>
    /// Gets the most characters of text a chunk of the conversation holds (a chunk always has at least one message), so a chunk of long messages and one of short tool calls are about equally heavy for the view to build.
    /// </summary>
    internal int ChunkCharacters { get; init; } = 20_000;

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
        private set
        {
            // What was sent before the work began is in it or done with it, so none of it can be what waits later.
            if (Set(ref isAiWorking, value) && value)
            {
                submitted.Clear();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the workspace runs a task another server delegated, which closes the message box altogether.
    /// </summary>
    public bool IsDelegated
    {
        get => isDelegated;
        set
        {
            if (Set(ref isDelegated, value))
            {
                if (value)
                {
                    IsSavedInputsOpen = false;
                }

                Raise(nameof(TakesTyping));
                ToggleSavedInputsCommand.Refresh();
                QueueCommand.Refresh();
                ResumeAutomationCommand.Refresh();
                StopRetryingCommand.Refresh();
                EditQueuedCommand.Refresh();
                ToggleAgendaCommitCommand.Refresh();
                AskDeleteQueuedCommand.Refresh();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the message box and its attach button can be used, which they cannot while <see cref="IsDelegated" />.
    /// </summary>
    public bool TakesTyping => !isDelegated;

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
    /// Gets a value indicating whether speech is being recorded, which the client shows as a screen of its own with the buttons that complete or cancel the recording.
    /// </summary>
    public bool IsRecording => recording;

    /// <summary>
    /// Gets how long the recording has gone on, as minutes and seconds.
    /// </summary>
    public string RecordingTime => recording ? $"{(int)(DateTime.UtcNow - recordingStarted).TotalMinutes}:{(DateTime.UtcNow - recordingStarted).Seconds:00}" : "0:00";

    /// <summary>
    /// Gets the tooltip of the voice input button.
    /// </summary>
    public string DictateTip => sending ? "Sending the recording" : "Voice input";

    /// <summary>
    /// Gets the command that starts recording speech and shows the recording screen; it is unavailable while the message box is blocked or a finished recording is still being sent.
    /// </summary>
    public AsyncCommand DictateCommand { get; }

    /// <summary>
    /// Gets the command that ends the recording and hands it to the server, which adds a draft that shows it being turned into text and fills it with the text when it is ready. The drafts open to show it.
    /// </summary>
    public AsyncCommand CompleteDictationCommand { get; }

    /// <summary>
    /// Gets the command that ends the recording and throws it away.
    /// </summary>
    public AsyncCommand CancelDictationCommand { get; }

    /// <summary>
    /// Gets the command that removes the <see cref="SavedInput" /> given as its parameter without taking it, which cancels a recording that is being turned into text and dismisses one that failed.
    /// </summary>
    public AsyncCommand DiscardSavedInputCommand { get; }

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
                ForgetEmptied();
                SaveInputCommand.Refresh();

                if (HasText)
                {
                    IsSavedInputsOpen = false;
                }

                SendCommand.Refresh();
                QueueCommand.Refresh();
                RefreshQuick();
                SaveDraft();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether anything but white space is typed in the message box, which offers saving it as a draft; without text the message box offers loading a draft instead.
    /// </summary>
    public bool HasText => !string.IsNullOrWhiteSpace(input);

    /// <summary>
    /// Gets the chat inputs saved in the workspace, which every client of the server sees: the queued ones first, in the order they are sent, then the others, the last saved first.
    /// </summary>
    public ObservableCollection<SavedInput> SavedInputs { get; } = [];

    /// <summary>
    /// Gets the saved inputs that are not queued, which the list of drafts shows.
    /// </summary>
    public ObservableCollection<SavedInput> Drafts { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the list of drafts is shown.
    /// </summary>
    public bool IsSavedInputsOpen
    {
        get => isSavedInputsOpen;
        private set
        {
            if (Set(ref isSavedInputsOpen, value))
            {
                if (value)
                {
                    IsUpNextOpen = false;
                }
                else
                {
                    DeletingInput = null;
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the list of what is up next is shown.
    /// </summary>
    public bool IsUpNextOpen
    {
        get => isUpNextOpen;
        set
        {
            if (Set(ref isUpNextOpen, value))
            {
                if (value)
                {
                    IsSavedInputsOpen = false;
                }
                else
                {
                    DeletingInput = null;
                }
            }
        }
    }

    /// <summary>
    /// Gets what the workspace is going to run by itself, as the server last told.
    /// </summary>
    public Agenda Agenda => agenda;

    /// <summary>
    /// Gets the items of the agenda as the Up Next list shows them, in the order they run.
    /// </summary>
    public ObservableCollection<AgendaRowViewModel> UpNext { get; } = [];

    /// <summary>
    /// Gets a value indicating whether nothing is up next.
    /// </summary>
    public bool HasNoUpNext => UpNext.Count == 0;

    /// <summary>
    /// Gets a value indicating whether the user stopped the work that runs by itself, which the status line offers to resume.
    /// </summary>
    public bool IsStopped => agenda.IsStopped;

    /// <summary>
    /// Gets a value indicating whether the status line shows above the conversation.
    /// </summary>
    public bool HasStatusLine => agenda.Status is not null || IsStopped || HasLimitPause || IsQueued;

    /// <summary>
    /// Gets what the status line says: what the server says, or a short word for what its buttons are about while the server says nothing.
    /// </summary>
    public string StatusText => agenda.Status ?? (IsStopped ? "Stopped" : HasLimitPause ? "Paused at the usage limit" : queue is { } waiting ? $"Waiting for Local AI, position {waiting.Position}" : string.Empty);

    /// <summary>
    /// Gets a value indicating whether the status line has any button.
    /// </summary>
    public bool HasStatusButtons => IsStopped || IsRetrying || HasLimitPause || IsQueued;

    /// <summary>
    /// Gets a value indicating whether work waits to be tried again, which the status line offers to stop.
    /// </summary>
    public bool IsRetrying => !IsStopped && agenda.Items.Any(item => item.Kind == AgendaKind.Retry);

    /// <summary>
    /// Gets the draft or queued input the user is asked whether to delete, or <see langword="null" /> when no such question shows.
    /// </summary>
    public SavedInput? DeletingInput
    {
        get => deletingInput;
        private set
        {
            if (Set(ref deletingInput, value))
            {
                Raise(nameof(IsAskingDelete));
                Raise(nameof(DeleteQuestion));
                Raise(nameof(DeletePreview));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the question whether to delete a draft or queued input shows.
    /// </summary>
    public bool IsAskingDelete => deletingInput is not null;

    /// <summary>
    /// Gets the question asked about <see cref="DeletingInput" />.
    /// </summary>
    public string DeleteQuestion => deletingInput switch
    {
        null => string.Empty,
        { IsQueued: true } => "Delete this queued input? It will not be sent.",
        { IsReady: false } => "Delete this recording? Its text is not kept.",
        _ => "Delete this draft? This cannot be undone."
    };

    /// <summary>
    /// Gets the text of <see cref="DeletingInput" />.
    /// </summary>
    public string DeletePreview => deletingInput?.Text ?? string.Empty;

    /// <summary>
    /// Gets whether the changes of the workspace are committed once the work of the AI is over.
    /// </summary>
    public AutoCommitState AutoCommit
    {
        get => autoCommit;
        private set
        {
            if (autoCommit != value)
            {
                autoCommit = value;
                RaiseAutoCommit();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the changes are to be committed once the work is over.
    /// </summary>
    public bool IsAutoCommitOn => autoCommit == AutoCommitState.On;

    /// <summary>
    /// Gets a value indicating whether the changes are being committed after the work.
    /// </summary>
    public bool IsCommitting => autoCommit == AutoCommitState.Committing;

    /// <summary>
    /// Gets what the button about committing the current work says.
    /// </summary>
    public string AutoCommitText => IsAutoCommitOn ? "When Done: Commit" : "When Done: Keep Changes";

    /// <summary>
    /// Gets what the auto commit button says it does.
    /// </summary>
    public string AutoCommitTip => IsAutoCommitOn ? "The changes are committed when the current work is done. Press to turn auto commit off." : "Auto commit the changes when the current work is done";

    /// <summary>
    /// Gets whether the changes are to be committed after the work of the draft or queued input that was loaded into the message box, which it took with it, or <see langword="null" /> when the box holds nothing that was loaded.
    /// </summary>
    public bool? LoadedAutoCommit
    {
        get => loadedAutoCommit;
        private set
        {
            if (Set(ref loadedAutoCommit, value))
            {
                _ = PersistAutoCommit(value);
                Raise(nameof(HasLoadedInput));
                Raise(nameof(IsLoadedAutoCommitOn));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the message box holds a draft or queued input that was loaded into it.
    /// </summary>
    public bool HasLoadedInput => loadedAutoCommit is not null;

    /// <summary>
    /// Gets a value indicating whether the loaded input is to be committed after.
    /// </summary>
    public bool IsLoadedAutoCommitOn => loadedAutoCommit == true;

    /// <summary>
    /// Gets how many queued inputs there are, which the Up Next button shows.
    /// </summary>
    public int QueuedCount => SavedInputs.Count(saved => saved.IsQueued);

    /// <summary>
    /// Gets a value indicating whether there is any queued input.
    /// </summary>
    public bool HasQueuedInputs => QueuedCount > 0;

    /// <summary>
    /// Gets the command that lets the work that runs by itself go on after it was stopped.
    /// </summary>
    public AsyncCommand ResumeAutomationCommand { get; }

    /// <summary>
    /// Gets the command that stops the work that waits to be tried again, so it is not tried again.
    /// </summary>
    public AsyncCommand StopRetryingCommand { get; }

    /// <summary>
    /// Gets the command that shows or hides the list of what is up next.
    /// </summary>
    public AsyncCommand ToggleUpNextCommand { get; }

    /// <summary>
    /// Gets the command that takes the queued input of the <see cref="AgendaRowViewModel" /> given as its parameter into the message box.
    /// </summary>
    public AsyncCommand EditQueuedCommand { get; }

    /// <summary>
    /// Gets the command that turns committing after the <see cref="AgendaRowViewModel" /> given as its parameter on or off.
    /// </summary>
    public AsyncCommand ToggleAgendaCommitCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to delete the queued input of the <see cref="AgendaRowViewModel" /> given as its parameter.
    /// </summary>
    public AsyncCommand AskDeleteQueuedCommand { get; }

    /// <summary>
    /// Gets the command that turns auto commit of the <see cref="SavedInput" /> given as its parameter on or off.
    /// </summary>
    public AsyncCommand ToggleSavedInputAutoCommitCommand { get; }

    /// <summary>
    /// Gets the command that queues what is typed and attached: it is sent once the work of the workspace is over, at once when there is none, ahead of further runs of a loop, and the changes are committed after it unless it was loaded with auto commit off.
    /// </summary>
    public AsyncCommand QueueCommand { get; }

    /// <summary>
    /// Gets the command that turns auto commit on or off for the work under way.
    /// </summary>
    public AsyncCommand ToggleAutoCommitCommand { get; }

    /// <summary>
    /// Gets the command that asks whether to delete the <see cref="SavedInput" /> given as its parameter.
    /// </summary>
    public AsyncCommand AskDeleteSavedInputCommand { get; }

    /// <summary>
    /// Gets the command that deletes <see cref="DeletingInput" />.
    /// </summary>
    public AsyncCommand ConfirmDeleteSavedInputCommand { get; }

    /// <summary>
    /// Gets the command that keeps <see cref="DeletingInput" />.
    /// </summary>
    public AsyncCommand CancelDeleteSavedInputCommand { get; }

    /// <summary>
    /// Gets a value indicating whether there is no draft, which the list says in place of its entries.
    /// </summary>
    public bool HasNoDrafts => Drafts.Count == 0;

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
                RaiseStatus();
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
    /// Gets or sets the work of the workspace that waits for the local AI, or <see langword="null" />.
    /// </summary>
    public QueueStatus? Queue
    {
        get => queue;
        private set
        {
            if (queue is not null && value is null)
            {
                submitted.Clear();
            }

            if (value is { Text.Length: > 0 })
            {
                // A prompt that has to wait is shown by what is up next; it is a message once its work starts.
                MessageParts waiting = MessageParts.Parse(value.Text);

                foreach (ChatMessage echo in Messages.Where(message => message.IsPending && Holds(waiting, MessageParts.Parse(message.Text))).ToList())
                {
                    Withdraw(echo);
                }
            }

            if (Set(ref queue, value))
            {
                Raise(nameof(IsQueued));
                RaiseStatus();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether work of the workspace waits for the local AI.
    /// </summary>
    public bool IsQueued => queue is not null;

    /// <summary>
    /// Gets the command that removes the waiting work. A prompt this client sent comes back to the message box without replacing what is typed there; one sent from elsewhere is kept as a draft.
    /// </summary>
    public AsyncCommand CancelQueuedCommand { get; }

    /// <summary>
    /// Gets what the automatic pause says: which window is at what share of its limit, what the maximum is and when the server looks again.
    /// </summary>
    public string LimitPauseText => limitPause is { } pause ? $"Paused automatically: the {pause.Window} usage window is at {pause.PercentUsed:0.#}%, at or past the maximum of {pause.MaxPercent:0.#}%. The AI resumes once it is below the maximum; the server checks again at {pause.NextCheckAt.ToLocalTime():t}." : string.Empty;

    /// <summary>
    /// Gets a value indicating whether the workspace has at least one draft, which the button of the drafts shows with a blue border.
    /// </summary>
    public bool HasDrafts => Drafts.Count > 0;

    /// <summary>
    /// Gets the command that saves <see cref="Input" /> as a draft on the server and clears it; it is available while text is typed.
    /// </summary>
    public AsyncCommand SaveInputCommand { get; }

    /// <summary>
    /// Gets the command that shows or hides the list of drafts.
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
    /// Gets or sets a value indicating whether the end of the conversation is what is on screen, which the view tells as it scrolls. Where it is not, reading the conversation again keeps what the user scrolled to, whether or not <see cref="AutoScroll" /> is on.
    /// </summary>
    public bool IsAtEnd { get; set; } = true;

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
    /// Shows a failure that happened outside the conversation, such as a refused stop, where the conversation shows its own.
    /// </summary>
    /// <param name="message">The failure.</param>
    public void Show(string message) => Error = message;

    /// <summary>
    /// Reads what is up next again, for a decision that cannot wait until the server tells of a change; what is known stays when it cannot be read.
    /// </summary>
    /// <returns>A task that completes once read.</returns>
    public async Task ReadAgenda()
    {
        try
        {
            if (await connection.GetAgenda(workspaceId, CancellationToken.None) is { } read)
            {
                ShowAgenda(read);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
        }
    }

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
            if (!await Attempt(async () => history = await connection.GetHistory(workspaceId, requested.ToString(CultureInfo.InvariantCulture), ChunkMessages, ChunkCharacters, CancellationToken.None)))
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
        int keep = 0;
        int weight = 0;

        for (int index = Messages.Count - 1; index >= 0 && keep < ChunkMessages; index--)
        {
            int length = Messages[index].Text.Length;

            if (keep > 0 && weight + length > ChunkCharacters)
            {
                break;
            }

            weight += length;
            keep++;
        }

        // A prompt on its way is no part of the stored chat, which what is dropped is counted in.
        int pending = FirstPending();
        int drop = Math.Min(Messages.Count - keep, pending < 0 ? Messages.Count : pending);

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

    /// <summary>
    /// Shows why a file chosen or pasted could not be attached.
    /// </summary>
    /// <param name="reason">Why it could not be read.</param>
    public void FailAttachment(string reason) => Error = $"The file could not be attached: {reason}";

    private int Position(string? cursor) => cursor is not null && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out int position) ? position : 0;

    private void KeepWindow()
    {
        if (autoScroll)
        {
            TrimToNewest();
        }
    }

    // Reading the conversation again is not new output, only what it holds beyond what was there is, so what a user scrolled to is read with it.
    private async Task Reload()
    {
        loading = true;
        ChatMessage? reach = autoScroll && IsAtEnd ? null : Messages.FirstOrDefault(message => !message.IsWork && !message.IsPending && !ReferenceEquals(message, streaming));
        ChatMessage? newest = Newest();

        try
        {
            await Load(reach, Math.Max(ChunkCharacters, Weight(Messages)));

            if (Newest() is { } now && (newest is null || !Reaches(now, newest)))
            {
                // What was read to keep a place the user scrolled to goes as new output takes the conversation back to its end.
                if (reach is not null)
                {
                    KeepWindow();
                }

                OutputArrived?.Invoke();
            }
        }
        finally
        {
            Flush();
        }
    }

    // Work is left out, since it comes and goes with the setting that shows it.
    private ChatMessage? Newest() => Messages.LastOrDefault(message => !message.IsWork && !message.IsPending && !ReferenceEquals(message, streaming));

    private void Stream(string piece, bool thought)
    {
        if (streaming is not null && streaming.IsThought != thought)
        {
            Unstream();
        }

        if (streaming is null)
        {
            streaming = new ChatMessage { Role = MessageRole.Assistant, Text = piece, Time = DateTimeOffset.UtcNow, IsWork = thought, IsThought = thought };
            Append(streaming);
            KeepWindow();
            return;
        }

        ChatMessage updated = streaming with { Text = streaming.Text + piece };
        Replace(streaming, updated);
        streaming = updated;
        KeepWindow();
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
            Append(replacement);
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
        List<ChatMessage> stored = [.. Messages.Where(message => !message.IsPending)];
        int overlap = Math.Min(added.Count, stored.Count);

        while (overlap > 0 && !Enumerable.Range(0, overlap).All(index => Same(stored[stored.Count - overlap + index], added[index])))
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

    private int Weight(IEnumerable<ChatMessage> messages) => messages.Sum(message => message.Text.Length);

    private async Task Load(ChatMessage? reach = null, int characters = 0)
    {
        ChatHistory? history = null;
        IReadOnlyList<SavedInput>? saved = null;
        await Attempt(async () => saved = await connection.GetSavedInputs(workspaceId, CancellationToken.None));
        Agenda? next = null;
        await Attempt(async () => next = await connection.GetAgenda(workspaceId, CancellationToken.None));

        if (await Attempt(async () => history = await connection.GetHistory(workspaceId, null, ChunkMessages, ChunkCharacters, CancellationToken.None)))
        {
            List<ChatMessage> window = [.. history!.Messages];
            string? older = history.Older;

            while (reach is not null && older is not null && !window.Any(message => Reaches(message, reach)) && Weight(window) < characters)
            {
                ChatHistory? more = null;

                if (!await Attempt(async () => more = await connection.GetHistory(workspaceId, older, ChunkMessages, ChunkCharacters, CancellationToken.None)))
                {
                    break;
                }

                window.InsertRange(0, more!.Messages);
                older = more.Older;
            }

            bool lost = reach is not null && !window.Any(message => Reaches(message, reach));
            streaming = null;
            // A prompt on its way stays, unless what was read holds the server's copy of it, one that was not shown before.
            List<ChatMessage> shown = [.. Messages.Where(message => !message.IsPending)];
            List<ChatMessage> unsent = [.. Messages.Where(message => message.IsPending && !window.Any(told => told.Role == MessageRole.User && told.Text == message.Text && !shown.Any(old => Reaches(told, old))))];
            Messages.Reset([.. window, .. unsent]);
            start = Position(older);
            Raise(nameof(HasMore));

            if (lost)
            {
                AutoScroll = true;
            }

            QueueStatus? waiting = null;

            // Known before the state, since work that waits for the local AI keeps the workspace from counting as ready.
            if (await Attempt(async () => waiting = await connection.GetQueuedWork(workspaceId, CancellationToken.None)))
            {
                Queue = waiting;
            }

            // Work still to come is shown before the state and that none is left only after it, so neither step looks like the end of the work.
            if (next?.IsPending == true)
            {
                ShowAgenda(next);
                next = null;
            }

            ShowWorkState(history.AutoCommit, history.Working);
            LimitPause = history.LimitPause;

            if (showWork != history.ShowWork)
            {
                showWork = history.ShowWork;
                Raise(nameof(ShowWork));
            }
        }

        if (saved is not null)
        {
            ShowSavedInputs(saved);
        }

        if (next is not null)
        {
            ShowAgenda(next);
        }
    }

    // Both are taken before either is told, so no state in between, such as ready, is ever seen.
    private void ShowWorkState(AutoCommitState committing, bool working)
    {
        bool commitChanged = autoCommit != committing;
        bool workingChanged = isAiWorking != working;
        autoCommit = committing;
        isAiWorking = working;

        if (workingChanged && working)
        {
            submitted.Clear();
        }

        if (commitChanged)
        {
            RaiseAutoCommit();
        }

        if (workingChanged)
        {
            Raise(nameof(IsAiWorking));
        }
    }

    private bool Reaches(ChatMessage message, ChatMessage wanted) => message.Role == wanted.Role && message.Text == wanted.Text && (message.Time is null || wanted.Time is null || message.Time == wanted.Time);

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

                    if (Arrived(message))
                    {
                        break;
                    }
                }

                if (streaming is not null && message.Role == MessageRole.Assistant && (message.IsThought ? streaming.IsThought : !message.IsWork && !streaming.IsThought))
                {
                    Replace(streaming, message);
                    streaming = null;
                }
                else
                {
                    if (streaming is not null && message.IsThought)
                    {
                        Unstream();
                    }

                    Append(message);
                    KeepWindow();
                }

                OutputArrived?.Invoke();
                break;
            case RemoteEventKind.MessageDelta when showWork && remoteEvent.Text is { Length: > 0 } piece:
                Stream(piece, remoteEvent.Thought == true);
                OutputArrived?.Invoke();
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

                // A notice is about something beside the work, whose answer goes on.
                if (remoteEvent.IsNotice != true)
                {
                    Unstream();
                }

                break;
            case RemoteEventKind.QueueChanged:
                Queue = remoteEvent.Queue;

                if (remoteEvent is { Queue: null, Text: "dropped" } && !IsAiWorking)
                {
                    QueuedWorkDropped?.Invoke();
                    PromptFailed?.Invoke();
                }

                break;
            case RemoteEventKind.LimitPauseChanged:
                LimitPause = remoteEvent.LimitPause;
                break;
            case RemoteEventKind.AutoCommitChanged when remoteEvent.AutoCommit is { } committing:
                AutoCommit = committing;
                break;
            case RemoteEventKind.SavedInputsChanged when remoteEvent.SavedInputs is { } saved:
                ShowSavedInputs(saved);
                break;
            case RemoteEventKind.AgendaChanged when remoteEvent.Agenda is { } next:
                ShowAgenda(next);
                break;
        }
    }

    private void RaiseAutoCommit()
    {
        Raise(nameof(AutoCommit));
        Raise(nameof(IsAutoCommitOn));
        Raise(nameof(IsCommitting));
        Raise(nameof(AutoCommitTip));
        Raise(nameof(AutoCommitText));
    }

    private void RaiseStatus()
    {
        Raise(nameof(HasStatusLine));
        Raise(nameof(StatusText));
        Raise(nameof(HasStatusButtons));
        Raise(nameof(IsRetrying));
    }

    // Rows that stay as they are keep their buttons, which would otherwise be made again with every change of another.
    private void ShowAgenda(Agenda next)
    {
        agenda = next;
        List<AgendaRowViewModel> rows = [.. next.Items.Select(item => new AgendaRowViewModel(item))];

        while (UpNext.Count > rows.Count)
        {
            UpNext.RemoveAt(UpNext.Count - 1);
        }

        for (int index = 0; index < rows.Count; index++)
        {
            if (index == UpNext.Count)
            {
                UpNext.Add(rows[index]);
            }
            else if (UpNext[index] != rows[index])
            {
                UpNext[index] = rows[index];
            }
        }

        Raise(nameof(HasNoUpNext));
        Raise(nameof(IsStopped));
        RaiseStatus();
        Raise(nameof(Agenda));
    }

    private SavedInput? QueuedInputOf(object? parameter) => parameter is AgendaRowViewModel { IsQueued: true } row ? SavedInputs.FirstOrDefault(saved => saved.Id == row.Item.Id) : null;

    private async Task ToggleAgendaCommit(object? parameter)
    {
        // The list is left to what the server tells, since it may run the item meanwhile.
        if (parameter is AgendaRowViewModel { HasCommitChoice: true } row)
        {
            Guid id = row.Item.Id!.Value;
            await Attempt(() => row.IsQueued
                ? connection.SetSavedInputAutoCommit(workspaceId, id, !row.IsCommitOn, CancellationToken.None)
                : connection.SetBringInCommit(workspaceId, id, !row.IsCommitOn, CancellationToken.None));
        }
    }

    private void ShowSavedInputs(IReadOnlyList<SavedInput> saved)
    {
        SavedInputs.Clear();
        Drafts.Clear();

        foreach (SavedInput entry in saved)
        {
            SavedInputs.Add(entry);

            if (!entry.IsQueued)
            {
                Drafts.Add(entry);
            }
        }

        // The question is dropped with what it asks about, which another client or the queue may have taken.
        if (deletingInput is { } asked && saved.All(entry => entry.Id != asked.Id))
        {
            DeletingInput = null;
        }

        Raise(nameof(HasNoDrafts));
        Raise(nameof(HasDrafts));
        Raise(nameof(QueuedCount));
        Raise(nameof(HasQueuedInputs));
    }

    private async Task SaveInput()
    {
        string text = input;
        bool commits = loadedAutoCommit ?? true;
        IReadOnlyList<SavedInput>? saved = null;

        if (!await Attempt(async () => saved = await connection.SaveInput(workspaceId, text, commits, CancellationToken.None)))
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
        if (parameter is not SavedInput { IsReady: true } chosen || takingSavedInput)
        {
            return;
        }

        takingSavedInput = true;
        SavedInput? taken = null;
        bool worked;

        try
        {
            worked = await Attempt(async () => taken = await connection.TakeSavedInput(workspaceId, chosen.Id, CancellationToken.None));
        }
        finally
        {
            takingSavedInput = false;
        }

        IsSavedInputsOpen = false;
        IsUpNextOpen = false;

        if (worked)
        {
            Input = HasText ? taken!.Text + "\n" + input : taken!.Text;
            LoadedAutoCommit = taken.AutoCommit;
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
            bool? loaded = await drafts.LoadAutoCommit(workspaceId, CancellationToken.None);

            if (input.Length == 0)
            {
                Input = saved;

                if (!string.IsNullOrWhiteSpace(saved))
                {
                    Restore(loaded);
                }
                else if (loaded is not null && loadedAutoCommit is null)
                {
                    // The input it came with is gone, with any attachments since those are not kept, so what is typed next is not taken for it.
                    await PersistAutoCommit(null);
                }
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

    // Kept with the draft, so a loaded input is not taken for a typed one after a restart.
    private async Task PersistAutoCommit(bool? loaded)
    {
        if (drafts is null)
        {
            return;
        }

        try
        {
            await drafts.SaveAutoCommit(workspaceId, loaded, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
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

    private void SetDictation(bool isRecording, bool isSending)
    {
        recording = isRecording;
        sending = isSending;
        Raise(nameof(IsRecording));
        Raise(nameof(RecordingTime));
        Raise(nameof(DictateTip));
        DictateCommand.Refresh();
        CompleteDictationCommand.Refresh();
        CancelDictationCommand.Refresh();
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

        recordingStarted = DateTime.UtcNow;
        SetDictation(true, false);
        CancellationTokenSource limit = new();
        recordingLimit = limit;
        _ = FollowRecording(limit.Token);
    }

    private async Task FollowRecording(CancellationToken cancellation)
    {
        try
        {
            while (DateTime.UtcNow - recordingStarted < longestRecording)
            {
                await Task.Delay(recordingTick, cancellation);
                Raise(nameof(RecordingTime));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await CompleteDictation();
    }

    private async Task CancelDictation()
    {
        recordingLimit?.Cancel();
        recordingLimit = null;
        SetDictation(false, false);

        try
        {
            await recorder!.Stop();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Error = exception.Message;
        }
    }

    private async Task CompleteDictation()
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

            IReadOnlyList<SavedInput>? saved = null;

            if (await Attempt(async () => saved = await connection.TranscribeSavedInput(workspaceId, Wav(pcm, recorder.SampleRate), CancellationToken.None)))
            {
                ShowSavedInputs(saved!);
                IsSavedInputsOpen = true;
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

    private async Task DiscardSavedInput(object? parameter)
    {
        if (parameter is not SavedInput entry || !discardingSavedInputs.Add(entry.Id))
        {
            return;
        }

        IReadOnlyList<SavedInput>? saved = null;

        try
        {
            if (await Attempt(async () => saved = await connection.DiscardSavedInput(workspaceId, entry.Id, CancellationToken.None)))
            {
                ShowSavedInputs(saved!);
            }
        }
        finally
        {
            discardingSavedInputs.Remove(entry.Id);
        }
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

    // Resuming gives the workspace work like queuing does, so its end is waited for in the same way.
    private async Task ResumeAutomation()
    {
        Notice = null;
        PromptStarted?.Invoke();

        if (!await Attempt(() => connection.ResumeAutomation(workspaceId, CancellationToken.None)))
        {
            PromptFailed?.Invoke();
        }
    }

    private Task ToggleAutoCommit()
    {
        bool wanted = !IsAutoCommitOn;
        return Attempt(() => connection.SetAutoCommit(workspaceId, wanted, CancellationToken.None));
    }

    /// <summary>
    /// Queues what is typed and attached.
    /// </summary>
    /// <param name="commit">Whether the changes are committed after its work.</param>
    /// <returns>A task that completes once it is queued, or has come back to the message box with the failure shown.</returns>
    // Queuing gives the workspace work like sending does, only that the server sends it when the work before it is over.
    public async Task QueueTyped(bool commit)
    {
        string text = input.Trim();
        List<ChatAttachment> sending = [.. Attachments];
        bool? loaded = loadedAutoCommit;
        Input = string.Empty;
        Attachments.Clear();
        Notice = null;
        PromptStarted?.Invoke();

        // The list is left to what the server tells, since it may send the input before this is answered.
        if (!await Attempt(async () =>
        {
            List<string> paths = [];

            foreach (ChatAttachment attachment in sending)
            {
                paths.Add(attachment.ServerPath ?? await connection.UploadAttachment(workspaceId, attachment.Name, attachment.Data, CancellationToken.None));
            }

            await connection.QueueInput(workspaceId, Written(text, paths), commit, CancellationToken.None);
        }))
        {
            PromptFailed?.Invoke();
            PutBack(text, sending);
            Restore(loaded);
        }
    }

    // A loaded input keeps what it was loaded with for as long as any of it is in the box.
    private void ForgetEmptied()
    {
        if (!HasContent)
        {
            LoadedAutoCommit = null;
        }
    }

    private void Restore(bool? loaded)
    {
        if (HasContent && loaded is not null)
        {
            LoadedAutoCommit = loaded;
        }
    }

    private async Task ToggleSavedInputAutoCommit(object? parameter)
    {
        // The list is left to what the server tells, since it may send a queued input meanwhile.
        if (parameter is SavedInput entry)
        {
            await Attempt(() => connection.SetSavedInputAutoCommit(workspaceId, entry.Id, !entry.AutoCommit, CancellationToken.None));
        }
    }

    private async Task Send()
    {
        string text = input.Trim();
        List<ChatAttachment> sending = [.. Attachments];
        bool? loaded = loadedAutoCommit;
        Input = string.Empty;
        Attachments.Clear();

        if (!await Deliver(text, sending, true, loaded))
        {
            PutBack(text, sending);
            Restore(loaded);
        }
    }

    // What was typed or attached while the prompt was on its way stays, after what comes back.
    private void PutBack(string text, IReadOnlyList<ChatAttachment> attached)
    {
        if (text.Length > 0)
        {
            Input = input.Length == 0 ? text : $"{text}\n{input}";
        }

        foreach ((ChatAttachment attachment, int index) in attached.Select((attachment, index) => (attachment, index)))
        {
            Attachments.Insert(Math.Min(index, Attachments.Count), attachment);
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

            await Deliver(text, [], false);
        }
    }

    private async Task<bool> Deliver(string text, List<ChatAttachment> sending, bool typed, bool? loaded = null)
    {
        Notice = null;
        PromptStarted?.Invoke();

        OverLimitAsk? refused = null;
        // Shown before the server is asked anything. A file not uploaded yet is shown by its name.
        ChatMessage echo = Echo(Written(text, sending.Select(attachment => attachment.ServerPath ?? attachment.Name)));

        bool sent = await Attempt(async () =>
        {
            List<string> paths = [];

            foreach (ChatAttachment attachment in sending)
            {
                paths.Add(attachment.ServerPath ?? await connection.UploadAttachment(workspaceId, attachment.Name, attachment.Data, CancellationToken.None));
            }

            string prompt = Written(text, paths);
            echo = Reword(echo, prompt);

            try
            {
                submitted.Add(MessageParts.Parse(prompt));

                await connection.Prompt(workspaceId, prompt, CancellationToken.None);
            }
            catch (RemoteException exception) when (exception.OverLimit is { } pause)
            {
                refused = new OverLimitAsk(pause, prompt, typed ? text : null, typed ? sending : [], loaded);
            }
        });

        if (refused is not null || !sent)
        {
            Withdraw(echo);
        }

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

    private string Written(string text, IEnumerable<string> attached) => string.Join('\n', (string[])[.. text.Length == 0 ? [] : (string[])[text], .. attached.Select(path => $"Attached file: {path}")]);

    private ChatMessage Echo(string prompt)
    {
        ChatMessage echo = new() { Role = MessageRole.User, Text = prompt, IsPending = true };
        Error = null;
        Messages.Add(echo);
        KeepWindow();
        // The user's own new message, which the view goes to as it does to any other output.
        OutputArrived?.Invoke();
        return echo;
    }

    private ChatMessage Reword(ChatMessage echo, string prompt)
    {
        int index = IndexOf(echo);

        if (index < 0 || echo.Text == prompt)
        {
            return echo;
        }

        ChatMessage reworded = echo with { Text = prompt };
        Messages[index] = reworded;
        return reworded;
    }

    private void Withdraw(ChatMessage echo)
    {
        int index = IndexOf(echo);

        if (index >= 0)
        {
            Messages.RemoveAt(index);
        }
    }

    // The server's copy of a prompt takes the place of the one shown while it was on its way. It is no new output: the user saw it already.
    private bool Arrived(ChatMessage told)
    {
        for (int index = 0; index < Messages.Count; index++)
        {
            if (Messages[index] is { IsPending: true } echo && echo.Text == told.Text)
            {
                if (index == FirstPending())
                {
                    Messages[index] = told;
                }
                else
                {
                    Messages.RemoveAt(index);
                    Append(told);
                }

                return true;
            }
        }

        return false;
    }

    // Prompts on their way stay at the end: whatever the server tells of before them came before them.
    private void Append(ChatMessage message)
    {
        int pending = FirstPending();

        if (pending < 0)
        {
            Messages.Add(message);
        }
        else
        {
            Messages.Insert(pending, message);
        }
    }

    private int FirstPending()
    {
        for (int index = 0; index < Messages.Count; index++)
        {
            if (Messages[index].IsPending)
            {
                return index;
            }
        }

        return -1;
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

        // What the prompt took from the message box leaves it, unless it was edited since.
        List<ChatAttachment> taken = [.. ask.Attached.Where(Attachments.Contains)];
        bool cleared = ask.Typed is { Length: > 0 } && input.Trim() == ask.Typed;

        if (cleared)
        {
            Input = string.Empty;
        }

        foreach (ChatAttachment attachment in taken)
        {
            Attachments.Remove(attachment);
        }

        PromptStarted?.Invoke();
        ChatMessage echo = Echo(ask.Prompt);

        if (!await Attempt(() => connection.PromptOverLimit(workspaceId, ask.Prompt, CancellationToken.None)))
        {
            Withdraw(echo);
            PutBack(cleared ? ask.Typed! : string.Empty, taken);
            Restore(ask.Loaded);
            PromptFailed?.Invoke();
        }
    }

    private async Task CancelQueued()
    {
        if (queue is not { } waiting)
        {
            return;
        }

        bool own = Submitted(MessageParts.Parse(waiting.Text));
        QueueStatus? removed = null;

        if (!await Attempt(async () => removed = await connection.CancelQueuedWork(workspaceId, !own, CancellationToken.None)))
        {
            return;
        }

        Queue = null;

        if (waiting.Delegated)
        {
            Notice = "The delegated task was cancelled.";
            return;
        }

        if (removed is not { Text.Length: > 0 })
        {
            return;
        }

        if (!own)
        {
            Notice = "The waiting prompt was kept as a draft.";
            return;
        }

        MessageParts parts = MessageParts.Parse(removed.Text);

        foreach (AttachedFile file in parts.Files)
        {
            Attachments.Add(new ChatAttachment { Name = file.Name, Data = ReadOnlyMemory<byte>.Empty, ServerPath = file.Path });
        }

        if (parts.Body.Length > 0)
        {
            Input = HasText ? parts.Body + "\n" + input : parts.Body;
        }
    }

    private bool Submitted(MessageParts queued) => submitted.Any(sent => Holds(queued, sent));

    // The server joins waiting prompts as paragraphs and keeps their attachment lines at the end.
    private bool Holds(MessageParts queued, MessageParts sent) => sent.Body.Length > 0
        ? $"\n\n{queued.Body}\n\n".Contains($"\n\n{sent.Body}\n\n", StringComparison.Ordinal)
        : sent.Files.Any(file => queued.Files.Any(other => other.Path == file.Path));

    private async Task CancelLimitPause()
    {
        if (await Attempt(() => connection.CancelLimitPause(workspaceId, CancellationToken.None)))
        {
            Notice = "The automatic resume was cancelled, so nothing goes on by itself. Send a message to start work again.";
        }
    }

    private sealed record OverLimitAsk(LimitPause Pause, string Prompt, string? Typed, IReadOnlyList<ChatAttachment> Attached, bool? Loaded);
}
