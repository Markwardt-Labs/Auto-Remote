namespace Markwardt.AutoRemote.Client;

/// <summary>
/// What the server's computer has and what the workspaces take of it, read over and over while any of it is on screen: in a resources section that is open, or in the workspace list.
/// </summary>
internal sealed class ResourcesViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a view model that reads the resources of a server.
    /// </summary>
    /// <param name="connection">The connection to the server.</param>
    /// <param name="interval">The time between reads; three seconds by default.</param>
    public ResourcesViewModel(IServerConnection connection, TimeSpan? interval = null)
    {
        this.connection = connection;
        this.interval = interval ?? TimeSpan.FromSeconds(3);
    }

    private readonly IServerConnection connection;

    private readonly TimeSpan interval;

    private readonly double lowMemoryShare = 0.1;

    private CancellationTokenSource? polling;

    private ResourceReport? report;

    private string? failure;

    private Guid? workspace;

    private bool isExpanded;

    private bool isSectionShown = true;

    private bool isListed;

    private bool isOnScreen = true;

    private bool isDisposed;

    /// <summary>
    /// Occurs when what was read changes.
    /// </summary>
    public event Action? Updated;

    /// <summary>
    /// Gets or sets a value indicating whether the section is expanded.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                Watch();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the settings that hold the section are showing.
    /// </summary>
    public bool IsSectionShown
    {
        get => isSectionShown;
        set
        {
            if (Set(ref isSectionShown, value))
            {
                Watch();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the workspace list is showing, whose working rows say what they take.
    /// </summary>
    public bool IsListed
    {
        get => isListed;
        set
        {
            if (Set(ref isListed, value))
            {
                Watch();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the screen this belongs to is the one showing.
    /// </summary>
    public bool IsOnScreen
    {
        get => isOnScreen;
        set
        {
            if (Set(ref isOnScreen, value))
            {
                Watch();
            }
        }
    }

    /// <summary>
    /// Gets or sets the workspace whose sessions the section lists, or <see langword="null" /> to list every workspace.
    /// </summary>
    public Guid? Workspace
    {
        get => workspace;
        set
        {
            if (Set(ref workspace, value))
            {
                RaiseReport();
            }
        }
    }

    /// <summary>
    /// Gets what is said in place of the rows, or <see langword="null" /> when there are rows.
    /// </summary>
    public string? Notice
        => failure is not null ? $"Resources could not be read: {failure}"
            : report is null ? "Reading…"
            : !report.Supported ? "This server can only measure resources on Linux."
            : workspace is not null && Shown() is null ? "Nothing is running in this workspace."
            : workspace is null && report.Workspaces.Count == 0 ? "No workspace is working."
            : null;

    /// <summary>
    /// Gets a value indicating whether <see cref="Notice" /> says anything.
    /// </summary>
    public bool HasNotice => Notice is not null;

    /// <summary>
    /// Gets the lines about the whole computer.
    /// </summary>
    public IReadOnlyList<string> MachineLines
    {
        get
        {
            if (report?.Machine is not { } machine)
            {
                return [];
            }

            List<string> lines = [$"Server computer memory: {Bytes(machine.TotalMemory - machine.AvailableMemory)} of {Bytes(machine.TotalMemory)} used, {Bytes(machine.AvailableMemory)} available"];

            if (machine.TotalSwap > 0)
            {
                lines.Add($"Swap: {Bytes(machine.TotalSwap - machine.FreeSwap)} of {Bytes(machine.TotalSwap)} used");
            }

            if (machine.Cpu is { } cpu)
            {
                lines.Add($"Processor: {Percent(cpu)} of {machine.Cores} {(machine.Cores == 1 ? "core" : "cores")}");
            }

            return lines;
        }
    }

    /// <summary>
    /// Gets the warning that the computer is running out of memory, or <see langword="null" /> when it is not.
    /// </summary>
    public string? Warning => report?.Machine is { } machine && machine.AvailableMemory < machine.TotalMemory * lowMemoryShare ? "The server computer is low on memory. Stop work that takes much of it, or the system may end the server." : null;

    /// <summary>
    /// Gets a value indicating whether <see cref="Warning" /> says anything.
    /// </summary>
    public bool HasWarning => Warning is not null;

    /// <summary>
    /// Gets the rows: the sessions of <see cref="Workspace" />, or every workspace followed by the server itself.
    /// </summary>
    public IReadOnlyList<ResourceRow> Rows => report is not { Supported: true } ? [] : workspace is null ? AllRows(report) : Shown() is { } shown ? SessionRows(shown) : [];

    /// <summary>
    /// Says in a few words what a working workspace takes.
    /// </summary>
    /// <param name="id">The workspace.</param>
    /// <param name="isWorking">Whether the workspace is known to be working.</param>
    /// <returns>The words, empty when the workspace is not working or nothing is known of it.</returns>
    public string Brief(Guid id, bool isWorking)
        => report?.Workspaces.FirstOrDefault(item => item.Id == id) is { Total.Processes: > 0 } found && (isWorking || found.IsWorking) ? Taken(found.Total.Memory, found.Total.Cpu) : string.Empty;

    /// <summary>
    /// Stops reading for good.
    /// </summary>
    public void Dispose()
    {
        isDisposed = true;
        Watch();
    }

    private void Watch()
    {
        bool wanted = !isDisposed && isOnScreen && (isListed || (isExpanded && isSectionShown));

        if (wanted && polling is null)
        {
            polling = new CancellationTokenSource();
            _ = Poll(polling.Token);
        }
        else if (!wanted && polling is not null)
        {
            polling.Cancel();
            polling.Dispose();
            polling = null;
            // What was read is not kept, since it would be old by the time it is shown again.
            report = null;
            failure = null;

            if (!isDisposed)
            {
                RaiseReport();
            }
        }
    }

    private async Task Poll(CancellationToken cancellation)
    {
        using PeriodicTimer timer = new(interval);

        try
        {
            do
            {
                await Refresh(cancellation);
            }
            while (await timer.WaitForNextTickAsync(cancellation));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Refresh(CancellationToken cancellation)
    {
        try
        {
            ResourceReport read = await connection.GetResources(cancellation);

            if (!cancellation.IsCancellationRequested)
            {
                report = read;
                failure = null;
                RaiseReport();
            }
        }
        // Whatever a read fails with, the reading goes on, since the connection reopens by itself.
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                report = null;
                failure = exception.Message;
                RaiseReport();
            }
        }
    }

    private WorkspaceResources? Shown() => report?.Workspaces.FirstOrDefault(item => item.Id == workspace);

    private IReadOnlyList<ResourceRow> SessionRows(WorkspaceResources shown)
    {
        List<ResourceRow> rows = [];

        if (shown.Sessions.Count > 1)
        {
            rows.Add(new ResourceRow { Name = "All sessions", Summary = Summary(shown.Total) });
        }

        rows.AddRange(shown.Sessions.Select(session => new ResourceRow { Name = session.Name, State = State(session.IsWorking), Summary = Summary(session.Usage), IsPart = shown.Sessions.Count > 1, Processes = Lines(session.Usage) }));
        return rows;
    }

    private IReadOnlyList<ResourceRow> AllRows(ResourceReport read)
    {
        List<ResourceRow> rows = [];

        foreach (WorkspaceResources item in read.Workspaces)
        {
            rows.Add(new ResourceRow { Name = item.Name, State = State(item.IsWorking), Summary = Summary(item.Total), Processes = Lines(item.Total) });

            // The sessions are only worth listing when there is more to the workspace than its main session.
            if (item.Sessions.Any(session => session.Id != item.Id))
            {
                rows.AddRange(item.Sessions.Select(session => new ResourceRow { Name = session.Name, State = State(session.IsWorking), Summary = Summary(session.Usage), IsPart = true }));
            }
        }

        rows.Add(new ResourceRow { Name = "Server itself", Summary = Summary(read.Server) });

        if (read.Other.Processes > 0)
        {
            rows.Add(new ResourceRow { Name = "Other server processes", Summary = Summary(read.Other), Processes = Lines(read.Other) });
        }

        return rows;
    }

    private string State(bool isWorking) => isWorking ? "Working" : "Not working";

    private string Summary(ResourceUsage usage) => usage.Processes == 0 ? "No processes" : $"{Taken(usage.Memory, usage.Cpu)} · {usage.Processes} {(usage.Processes == 1 ? "process" : "processes")}";

    private IReadOnlyList<ResourceProcessLine> Lines(ResourceUsage usage) => [.. usage.Heaviest.Select(process => new ResourceProcessLine { Command = process.Command, Usage = Taken(process.Memory, process.Cpu) })];

    private string Taken(long memory, double? cpu) => cpu is { } used ? $"{Bytes(memory)} · {Percent(used)} CPU" : Bytes(memory);

    private string Bytes(long bytes)
    {
        double megabytes = bytes / (1024.0 * 1024.0);
        return megabytes >= 1024 ? string.Create(CultureInfo.InvariantCulture, $"{megabytes / 1024:0.0} GB")
            : bytes > 0 && megabytes < 1 ? "<1 MB"
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(megabytes):0} MB");
    }

    private string Percent(double percent) => percent is > 0 and < 1 ? "<1%" : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(percent):0}%");

    private void RaiseReport()
    {
        Raise(nameof(Notice));
        Raise(nameof(HasNotice));
        Raise(nameof(MachineLines));
        Raise(nameof(Warning));
        Raise(nameof(HasWarning));
        Raise(nameof(Rows));
        Updated?.Invoke();
    }
}
