namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The settings of a loop of the AI in a workspace, as Send Options takes them for Repeat: the prompt (the review prompt, the expand prompt or what is typed in the message box), how often it is sent, when the loop ends by itself and whether every run is committed.
/// </summary>
internal sealed class LoopViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LoopViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace.</param>
    public LoopViewModel(IServerConnection connection, WorkspaceInfo workspace)
    {
        this.connection = connection;
        this.workspace = workspace;
        selectedCondition = All.First(choice => choice.Condition == LoopCondition.Forever);
    }

    private readonly IServerConnection connection;

    private readonly WorkspaceInfo workspace;

    private string reviewPrompt = string.Empty;

    private string expandPrompt = string.Empty;

    private LoopPromptKind kind = LoopPromptKind.Review;

    private decimal? intervalMinutes = 15;

    private LoopConditionChoice selectedCondition;

    private TimeSpan? endTime = new(TimeSpan.FromHours(23).Ticks);

    private decimal? hours = 2;

    private decimal? minutes = 0;

    private decimal? runs = 5;

    private IReadOnlyList<string> windows = [];

    private string? selectedWindow;

    private decimal? percent = 90;

    private bool commitEachRun;

    /// <summary>
    /// Gets the prompt of the review choice, which is the review prompt of the workspace or the global one, as read by <see cref="Prepare" />.
    /// </summary>
    public string ReviewPrompt
    {
        get => reviewPrompt;
        private set => Set(ref reviewPrompt, value);
    }

    /// <summary>
    /// Gets the prompt of the expand choice, which is the expand prompt of the workspace or the global one, as read by <see cref="Prepare" />.
    /// </summary>
    public string ExpandPrompt
    {
        get => expandPrompt;
        private set => Set(ref expandPrompt, value);
    }

    /// <summary>
    /// Gets the ways a loop can end by itself: the ones that need a usage window only when the AI service reports some.
    /// </summary>
    public IReadOnlyList<LoopConditionChoice> Conditions => [.. All.Where(choice => choice.Condition != LoopCondition.UntilWindow || windows.Count > 0)];

    /// <summary>
    /// Gets the usage windows the AI service reports, which a loop can watch.
    /// </summary>
    public IReadOnlyList<string> Windows => windows;

    /// <summary>
    /// Gets or sets a value indicating whether every run sends the review prompt. Setting it to <see langword="false" /> changes nothing: another choice is made by setting that one.
    /// </summary>
    public bool UseReview
    {
        get => kind == LoopPromptKind.Review;
        set => Choose(LoopPromptKind.Review, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether every run sends the expand prompt. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool UseExpand
    {
        get => kind == LoopPromptKind.Expand;
        set => Choose(LoopPromptKind.Expand, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether every run sends what is typed in the message box. Setting it to <see langword="false" /> changes nothing.
    /// </summary>
    public bool UseCustom
    {
        get => kind == LoopPromptKind.Custom;
        set => Choose(LoopPromptKind.Custom, value);
    }

    /// <summary>
    /// Gets or sets the minutes from the start of one run to the start of the next; zero, or nothing (an empty box), runs again as soon as the last run is done.
    /// </summary>
    public decimal? IntervalMinutes
    {
        get => intervalMinutes;
        set => Set(ref intervalMinutes, value);
    }

    /// <summary>
    /// Gets or sets when the loop ends by itself.
    /// </summary>
    public LoopConditionChoice SelectedCondition
    {
        get => selectedCondition;
        set
        {
            if (value is not null && Set(ref selectedCondition, value))
            {
                Raise(nameof(IsUntilTime));
                Raise(nameof(IsDuration));
                Raise(nameof(IsRuns));
                Raise(nameof(IsWindow));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the loop ends at a time of day.
    /// </summary>
    public bool IsUntilTime => selectedCondition.Condition == LoopCondition.UntilTime;

    /// <summary>
    /// Gets a value indicating whether the loop ends after a length of time.
    /// </summary>
    public bool IsDuration => selectedCondition.Condition == LoopCondition.ForDuration;

    /// <summary>
    /// Gets a value indicating whether the loop ends after a number of runs.
    /// </summary>
    public bool IsRuns => selectedCondition.Condition == LoopCondition.Runs;

    /// <summary>
    /// Gets a value indicating whether the loop ends when a usage window reaches a share of its limit.
    /// </summary>
    public bool IsWindow => selectedCondition.Condition == LoopCondition.UntilWindow;

    /// <summary>
    /// Gets or sets the time of day the loop ends, the next time it is that time.
    /// </summary>
    public TimeSpan? EndTime
    {
        get => endTime;
        set => Set(ref endTime, value);
    }

    /// <summary>
    /// Gets or sets the hours part of the length of time the loop runs.
    /// </summary>
    public decimal? Hours
    {
        get => hours;
        set => Set(ref hours, value);
    }

    /// <summary>
    /// Gets or sets the minutes part of the length of time the loop runs.
    /// </summary>
    public decimal? Minutes
    {
        get => minutes;
        set => Set(ref minutes, value);
    }

    /// <summary>
    /// Gets or sets how many runs the loop makes.
    /// </summary>
    public decimal? Runs
    {
        get => runs;
        set => Set(ref runs, value);
    }

    /// <summary>
    /// Gets or sets the usage window the loop watches.
    /// </summary>
    public string? SelectedWindow
    {
        get => selectedWindow;
        set => Set(ref selectedWindow, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether the changes are committed after each run that ends well.
    /// </summary>
    public bool CommitEachRun
    {
        get => commitEachRun;
        set => Set(ref commitEachRun, value);
    }

    /// <summary>
    /// Gets or sets the share of the window's limit, in percent, at which the loop ends.
    /// </summary>
    public decimal? Percent
    {
        get => percent;
        set => Set(ref percent, value);
    }

    private IReadOnlyList<LoopConditionChoice> All { get; } =
    [
        new(LoopCondition.Forever, "Until I cancel it"),
        new(LoopCondition.UntilTime, "Until a time of day"),
        new(LoopCondition.ForDuration, "For a length of time"),
        new(LoopCondition.Runs, "For a number of runs"),
        new(LoopCondition.UntilWindow, "Until a usage limit reaches a share")
    ];

    /// <summary>
    /// Puts the settings back to what they start from, and reads the review and expand prompts and which usage windows the AI service reports so the loop can watch one.
    /// </summary>
    /// <param name="typed">Whether something is typed in the message box, which every run then sends unless another prompt is chosen.</param>
    /// <param name="chosen">The prompt every run sends from the start, or <see langword="null" /> for the one <paramref name="typed" /> gives.</param>
    /// <returns>A task that completes once everything is read.</returns>
    public async Task Prepare(bool typed, LoopPromptKind? chosen = null)
    {
        Choose(chosen ?? (typed ? LoopPromptKind.Custom : LoopPromptKind.Review), true);
        IntervalMinutes = 15;
        EndTime = new TimeSpan(TimeSpan.FromHours(23).Ticks);
        Hours = 2;
        Minutes = 0;
        Runs = 5;
        Percent = 90;
        CommitEachRun = false;
        IReadOnlyList<string> names = [];

        try
        {
            WorkspaceSettings settings = await connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);
            ReviewPrompt = (settings.ReviewPrompt ?? settings.GlobalReviewPrompt).Trim();
            ExpandPrompt = (settings.ExpandPrompt ?? settings.GlobalExpandPrompt).Trim();
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or ObjectDisposedException or InvalidDataException)
        {
        }

        try
        {
            names = [.. (await connection.GetUsage(workspace.Service, CancellationToken.None)).Windows.Select(window => window.Name)];
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or ObjectDisposedException or InvalidDataException)
        {
        }

        windows = names;
        selectedWindow = names.Contains(selectedWindow ?? string.Empty) ? selectedWindow : names.FirstOrDefault();

        SelectedCondition = All.First(choice => choice.Condition == LoopCondition.Forever);

        Raise(nameof(Conditions));
        Raise(nameof(Windows));
        Raise(nameof(SelectedWindow));
    }

    /// <summary>
    /// Builds the settings of the loop.
    /// </summary>
    /// <param name="now">The current time, which a time of day is counted from.</param>
    /// <param name="typed">What is typed in the message box.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidOperationException">What is chosen is not enough for a loop; the message says what is missing.</exception>
    public LoopSettings Build(DateTimeOffset now, string typed)
    {
        string prompt = kind switch
        {
            LoopPromptKind.Review => ReviewPrompt,
            LoopPromptKind.Expand => ExpandPrompt,
            _ => typed.Trim(),
        };

        if (prompt.Length == 0)
        {
            throw new InvalidOperationException(kind switch
            {
                LoopPromptKind.Review => "The review prompt could not be read, so write the prompt the loop sends.",
                LoopPromptKind.Expand => "The expand prompt could not be read, so write the prompt the loop sends.",
                _ => "Write the prompt the loop sends.",
            });
        }

        decimal interval = intervalMinutes ?? 0;

        if (interval is < 0 or > 1440)
        {
            throw new InvalidOperationException("The minutes between runs must be between 0 and 1440.");
        }

        LoopSettings settings = new() { Prompt = prompt, IntervalSeconds = (int)Math.Round(interval * 60), Condition = selectedCondition.Condition, AutoCommit = commitEachRun };

        switch (selectedCondition.Condition)
        {
            case LoopCondition.UntilTime:
                DateTimeOffset end = new DateTimeOffset(now.LocalDateTime.Date + (endTime ?? throw new InvalidOperationException("Choose the time the loop ends.")));
                return settings with { Until = end <= now ? end.AddDays(1) : end };
            case LoopCondition.ForDuration:
                int seconds = (int)(((hours ?? 0) * 3600) + ((minutes ?? 0) * 60));
                return seconds is > 0 and <= 24 * 86400 ? settings with { DurationSeconds = seconds } : throw new InvalidOperationException("The loop must run for a time between a minute and 24 days.");
            case LoopCondition.Runs:
                return runs is >= 1 ? settings with { Runs = (int)runs.Value } : throw new InvalidOperationException("The loop needs at least one run.");
            case LoopCondition.UntilWindow:
                return selectedWindow is { } window && percent is >= 1 and <= 100 ? settings with { Window = window, Percent = (double)percent.Value } : throw new InvalidOperationException("Choose a usage window and a share between 1 and 100.");
            default:
                return settings;
        }
    }

    private void Choose(LoopPromptKind chosen, bool selected)
    {
        if (selected && kind != chosen)
        {
            kind = chosen;
            Raise(nameof(UseReview));
            Raise(nameof(UseExpand));
            Raise(nameof(UseCustom));
        }
    }
}
