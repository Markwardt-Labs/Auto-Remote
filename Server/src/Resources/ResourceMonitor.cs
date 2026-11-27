namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Measures what the server's computer has and how much of it the sessions of the workspaces take.
/// </summary>
internal interface IResourceMonitor
{
    /// <summary>
    /// Starts keeping track of which session each process belongs to, which has to happen while the processes that started it are still there.
    /// </summary>
    /// <param name="cancellation">Ends the tracking when cancelled.</param>
    void Open(CancellationToken cancellation);

    /// <summary>
    /// Reads what is taken now.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The report.</returns>
    Task<ResourceReport> Read(CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ResourceMonitor(IProcessSource processes, IShellCommandLog commands, IWorkspaceManager workspaces, IWorkspaceActivity activity, IScriptRunner scripts, TimeProvider? time = null) : IResourceMonitor
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    private readonly SemaphoreSlim gate = new(1, 1);

    private readonly TimeSpan freshness = TimeSpan.FromSeconds(1.5);

    private readonly TimeSpan shortestWindow = TimeSpan.FromSeconds(1);

    private readonly TimeSpan busyInterval = TimeSpan.FromSeconds(2);

    private readonly int idleBeats = 5;

    private readonly int listedProcesses = 5;

    private readonly int listedBusyProcesses = 3;

    private readonly double busyShare = 1;

    private readonly int commandLength = 200;

    private Dictionary<(int Id, long Started), Tracked> tracked = [];

    private Dictionary<(int Id, long Started), long> seen = [];

    private MachineSample? machine;

    private double? machineCpu;

    private Tracked? server;

    private DateTimeOffset? sampledAt;

    private ResourceReport? report;

    private DateTimeOffset reportedAt;

    /// <inheritdoc />
    public void Open(CancellationToken cancellation)
    {
        if (processes.IsSupported)
        {
            _ = Watch(cancellation);
        }
    }

    /// <inheritdoc />
    public async Task<ResourceReport> Read(CancellationToken cancellation)
    {
        if (!processes.IsSupported)
        {
            return new ResourceReport { Supported = false };
        }

        await gate.WaitAsync(cancellation);

        try
        {
            if (report is not null && clock.GetUtcNow() - reportedAt < freshness)
            {
                return report;
            }

            IReadOnlyList<WorkspaceInfo> sessions = await workspaces.All(cancellation);
            Sample(sessions);
            report = Report(sessions);
            reportedAt = clock.GetUtcNow();
            return report;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task Watch(CancellationToken cancellation)
    {
        int beats = 0;

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Track(beats == 0, cancellation);
                beats = (beats + 1) % idleBeats;
                await Task.Delay(busyInterval, clock, cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Work is followed closely, since what its processes leave behind can only be told apart while they are still there.
    private async Task Track(bool isDue, CancellationToken cancellation)
    {
        try
        {
            IReadOnlyList<WorkspaceInfo> sessions = await workspaces.All(cancellation);

            if (!isDue && !sessions.Any(session => IsBusy(session.Id)))
            {
                return;
            }

            await gate.WaitAsync(cancellation);

            try
            {
                Sample(sessions);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private bool IsBusy(Guid id) => activity.Get(id) != WorkspaceState.Ready || scripts.IsRunning(id);

    private void Sample(IReadOnlyList<WorkspaceInfo> sessions)
    {
        // The processor use is what changed since the sample before, which says nothing when hardly any time has passed.
        if (sampledAt is { } last && clock.GetUtcNow() - last < shortestWindow)
        {
            return;
        }

        Dictionary<int, ProcessEntry> running = processes.List().ToDictionary(process => process.Id);
        MachineSample? current = processes.Machine();
        long window = current is not null && machine is not null ? current.TotalTicks - machine.TotalTicks : 0;
        machineCpu = window > 0 ? Share(current!.BusyTicks - machine!.BusyTicks, window) : null;

        Dictionary<int, ProcessEntry> own = Collect(running);
        Dictionary<int, Tracked> placed = [];
        Dictionary<int, Guid> scriptProcesses = scripts.Processes().DistinctBy(item => item.ProcessId).ToDictionary(item => item.ProcessId, item => item.Id);
        Placement placement = new(own, placed, scriptProcesses, sessions);

        foreach (ProcessEntry process in own.Values)
        {
            Place(placement, process).Cpu = Rate(process, window);
        }

        tracked = placed.Values.ToDictionary(item => (item.Process.Id, item.Process.Started));
        server = running.TryGetValue(processes.ServerId, out ProcessEntry? itself) ? new Tracked(itself) { Cpu = Rate(itself, window) } : null;
        seen = running.Values.ToDictionary(process => (process.Id, process.Started), process => process.Ticks);
        machine = current;
        sampledAt = clock.GetUtcNow();
    }

    private double? Rate(ProcessEntry process, long window)
    {
        if (window <= 0)
        {
            return null;
        }

        // A process that was not there at the sample before has used all of its processor time since then.
        return Share(process.Ticks - seen.GetValueOrDefault((process.Id, process.Started)), window);
    }

    private double Share(long part, long whole) => Math.Clamp(part * 100.0 / whole, 0, 100);

    private Dictionary<int, ProcessEntry> Collect(Dictionary<int, ProcessEntry> running)
    {
        ILookup<int, ProcessEntry> children = running.Values.ToLookup(process => process.ParentId);
        IReadOnlySet<int> members = processes.ServiceMembers();
        Queue<ProcessEntry> waiting = new(running.Values.Where(process => process.ParentId == processes.ServerId || members.Contains(process.Id) || tracked.ContainsKey((process.Id, process.Started))));
        Dictionary<int, ProcessEntry> own = [];

        while (waiting.TryDequeue(out ProcessEntry? process))
        {
            if (process.Id != processes.ServerId && own.TryAdd(process.Id, process))
            {
                foreach (ProcessEntry child in children[process.Id])
                {
                    waiting.Enqueue(child);
                }
            }
        }

        return own;
    }

    private Tracked Place(Placement placement, ProcessEntry process)
    {
        if (placement.Placed.TryGetValue(process.Id, out Tracked? done))
        {
            return done;
        }

        Tracked item = new(process);
        placement.Placed[process.Id] = item;
        Tracked? previous = tracked.GetValueOrDefault((process.Id, process.Started));
        Tracked? parent = placement.Own.TryGetValue(process.ParentId, out ProcessEntry? above) ? Place(placement, above) : null;

        if (parent?.Owner is { } inherited)
        {
            item.Own(inherited, true);
        }
        else if (previous is { IsCertain: true, Owner: { } kept })
        {
            item.Own(kept, true);
        }
        else if (placement.Scripts.TryGetValue(process.Id, out Guid script) && Find(placement, script) is { } scripted)
        {
            item.Own(scripted, true);
        }
        else
        {
            PlaceByItself(placement, item, previous);
        }

        return item;
    }

    private void PlaceByItself(Placement placement, Tracked item, Tracked? previous)
    {
        string? folder = processes.Folder(item.Process.Id);

        if (processes.Arguments(item.Process.Id) is ["/bin/sh", "-c", var command])
        {
            List<WorkspaceInfo> ran = [.. commands.Find(command).Select(id => placement.Sessions.FirstOrDefault(session => session.Id == id)).OfType<WorkspaceInfo>()];

            if ((ran.FirstOrDefault(session => Holds(session.Folder, folder)) ?? ran.FirstOrDefault()) is { } session)
            {
                item.Own(Describe(session), true);
                return;
            }
        }

        // Where a process was first found is kept, since it may move to another folder afterwards.
        if (previous?.Owner is { } known)
        {
            item.Own(known, false);
        }
        else if (placement.Sessions.Where(session => Holds(session.Folder, folder)).MaxBy(session => session.Folder.Length) is { } inside)
        {
            item.Own(Describe(inside), false);
        }
    }

    private bool Holds(string session, string? folder)
    {
        string root = Path.TrimEndingDirectorySeparator(session);
        return folder is not null && (folder == root || folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private Owner? Find(Placement placement, Guid id) => placement.Sessions.FirstOrDefault(session => session.Id == id) is { } session ? Describe(session) : null;

    private Owner Describe(WorkspaceInfo session)
        => new(session.Id, session.Side?.ParentId ?? session.Id, session.Side?.Name ?? "Main");

    private ResourceReport Report(IReadOnlyList<WorkspaceInfo> sessions)
    {
        Dictionary<Guid, WorkspaceInfo> byId = sessions.ToDictionary(session => session.Id);
        List<(Owner? Owner, ProcessUsage Usage)> measured = [];

        foreach (Tracked item in tracked.Values)
        {
            ProcessUsage usage = Measure(item);

            // One that ended since the sample takes nothing anymore.
            if (usage.Memory > 0)
            {
                measured.Add((item.Owner is { } placed ? Current(placed, byId) : null, usage));
            }
        }

        Dictionary<Guid, Owner> owners = sessions.Where(session => IsBusy(session.Id)).Select(Describe).Concat(measured.Select(item => item.Owner).OfType<Owner>()).DistinctBy(owner => owner.Session).ToDictionary(owner => owner.Session);
        List<WorkspaceResources> result = [];

        foreach (IGrouping<Guid, Owner> group in owners.Values.GroupBy(owner => owner.Workspace))
        {
            if (!byId.TryGetValue(group.Key, out WorkspaceInfo? workspace))
            {
                continue;
            }

            List<SessionResources> parts = [.. group.OrderBy(owner => owner.Session != owner.Workspace).ThenBy(owner => owner.Name, StringComparer.OrdinalIgnoreCase).Select(owner => new SessionResources { Id = owner.Session, Name = owner.Name, IsWorking = IsBusy(owner.Session), Usage = Total(measured.Where(item => item.Owner?.Session == owner.Session).Select(item => item.Usage)) })];
            result.Add(new WorkspaceResources { Id = workspace.Id, Name = workspace.Name, IsWorking = parts.Any(part => part.IsWorking), Total = Total(measured.Where(item => item.Owner?.Workspace == workspace.Id).Select(item => item.Usage)), Sessions = parts });
        }

        return new ResourceReport
        {
            Supported = true,
            Machine = machine is null ? null : new MachineResources { TotalMemory = machine.TotalMemory, AvailableMemory = machine.AvailableMemory, TotalSwap = machine.TotalSwap, FreeSwap = machine.FreeSwap, Cores = machine.Cores, Cpu = machineCpu },
            Workspaces = [.. result.OrderByDescending(workspace => workspace.Total.Memory).ThenBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)],
            Server = Total(server is null ? [] : [Measure(server)]),
            Other = Total(measured.Where(item => item.Owner is null).Select(item => item.Usage)),
        };
    }

    // A session that is gone leaves its processes with the workspace it was part of.
    private Owner? Current(Owner placed, Dictionary<Guid, WorkspaceInfo> sessions)
        => !sessions.ContainsKey(placed.Workspace) ? null : sessions.TryGetValue(placed.Session, out WorkspaceInfo? session) ? Describe(session) : placed with { Name = $"{placed.Name} (removed)" };

    private ProcessUsage Measure(Tracked item)
        => new() { Id = item.Process.Id, Command = CommandLine(item.Process.Id), Memory = processes.Memory(item.Process.Id), Cpu = item.Cpu };

    private string CommandLine(int id)
    {
        IReadOnlyList<string> arguments = processes.Arguments(id);

        if (arguments.Count == 0)
        {
            return $"process {id}";
        }

        string line = string.Join(' ', arguments.Skip(1).Prepend(Path.GetFileName(arguments[0]) is { Length: > 0 } name ? name : arguments[0])).ReplaceLineEndings(" ");
        return line.Length > commandLength ? $"{line[..commandLength]}…" : line;
    }

    private ResourceUsage Total(IEnumerable<ProcessUsage> usages)
    {
        List<ProcessUsage> all = [.. usages.OrderByDescending(usage => usage.Memory)];
        // What keeps the processor busy is listed too, however little memory it takes.
        HashSet<ProcessUsage> heaviest = [.. all.Take(listedProcesses), .. all.Where(usage => usage.Cpu >= busyShare).OrderByDescending(usage => usage.Cpu).Take(listedBusyProcesses)];
        return new ResourceUsage
        {
            Memory = all.Sum(usage => usage.Memory),
            Cpu = all.Any(usage => usage.Cpu is not null) ? Math.Min(100, all.Sum(usage => usage.Cpu ?? 0)) : null,
            Processes = all.Count,
            Heaviest = [.. all.Where(heaviest.Contains)],
        };
    }

    private sealed record Owner(Guid Session, Guid Workspace, string Name);

    private sealed record Placement(Dictionary<int, ProcessEntry> Own, Dictionary<int, Tracked> Placed, Dictionary<int, Guid> Scripts, IReadOnlyList<WorkspaceInfo> Sessions);

    private sealed class Tracked(ProcessEntry process)
    {
        public ProcessEntry Process { get; } = process;

        public Owner? Owner { get; private set; }

        public bool IsCertain { get; private set; }

        public double? Cpu { get; set; }

        public void Own(Owner owner, bool certain)
        {
            Owner = owner;
            IsCertain = certain;
        }
    }
}
