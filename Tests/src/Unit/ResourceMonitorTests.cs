namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ResourceMonitorTests
{
    private readonly int server = 100;

    private readonly Mock<IProcessSource> source = new();

    private readonly Mock<IShellCommandLog> commands = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceActivity> activity = new();

    private readonly Mock<IScriptRunner> scripts = new();

    private readonly ManualTime time = new();

    private readonly Dictionary<int, FakeProcess> processes = [];

    private readonly List<WorkspaceInfo> sessions = [];

    private readonly HashSet<int> members = [];

    private readonly HashSet<Guid> working = [];

    private readonly List<(Guid Id, int ProcessId)> scriptProcesses = [];

    private readonly WorkspaceInfo orchard;

    private readonly WorkspaceInfo harbor;

    private readonly ResourceMonitor monitor;

    private long totalTicks = 1000;

    private long busyTicks = 100;

    private long available = 8000;

    public ResourceMonitorTests()
    {
        orchard = Add(new WorkspaceInfo { Id = Guid.NewGuid(), Folder = "/work/orchard" });
        harbor = Add(new WorkspaceInfo { Id = Guid.NewGuid(), Folder = "/work/harbor/" });
        processes[server] = new FakeProcess { Parent = 1, Memory = 500, Arguments = ["/opt/auto-remote/server"], Folder = "/opt/auto-remote" };
        source.SetupGet(x => x.IsSupported).Returns(true);
        source.SetupGet(x => x.ServerId).Returns(server);
        source.Setup(x => x.List()).Returns(() => [.. processes.Select(item => new ProcessEntry { Id = item.Key, ParentId = item.Value.Parent, Started = item.Value.Started, Ticks = item.Value.Ticks })]);
        source.Setup(x => x.ServiceMembers()).Returns(() => members);
        source.Setup(x => x.Arguments(It.IsAny<int>())).Returns((int id) => processes.TryGetValue(id, out FakeProcess? found) ? found.Arguments : []);
        source.Setup(x => x.Folder(It.IsAny<int>())).Returns((int id) => processes.GetValueOrDefault(id)?.Folder);
        source.Setup(x => x.Memory(It.IsAny<int>())).Returns((int id) => processes.GetValueOrDefault(id)?.Memory ?? 0);
        source.Setup(x => x.Machine()).Returns(() => new MachineSample { TotalMemory = 16000, AvailableMemory = available, TotalSwap = 2000, FreeSwap = 1500, Cores = 4, TotalTicks = totalTicks, BusyTicks = busyTicks });
        commands.Setup(x => x.Find(It.IsAny<string>())).Returns([]);
        workspaces.Setup(x => x.All(It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. sessions]);
        activity.Setup(x => x.Get(It.IsAny<Guid>())).Returns((Guid id) => working.Contains(id) ? WorkspaceState.Working : WorkspaceState.Ready);
        scripts.Setup(x => x.Processes()).Returns(() => [.. scriptProcesses]);
        scripts.Setup(x => x.IsRunning(It.IsAny<Guid>())).Returns((Guid id) => scriptProcesses.Any(item => item.Id == id));
        monitor = new ResourceMonitor(source.Object, commands.Object, workspaces.Object, activity.Object, scripts.Object, time);
    }

    private WorkspaceInfo Add(WorkspaceInfo workspace)
    {
        sessions.Add(workspace);
        return workspace;
    }

    private WorkspaceInfo AddSide(WorkspaceInfo parent, string name, string folder)
        => Add(new WorkspaceInfo { Id = Guid.NewGuid(), Folder = folder, Side = new SideSession { ParentId = parent.Id, ParentName = parent.Name, Name = name, State = SideSessionState.Ready, StartedFrom = SideSessionStart.Commit } });

    private void Start(int id, int parent, long memory, string folder, params string[] arguments)
        => processes[id] = new FakeProcess { Parent = parent, Started = id, Memory = memory, Folder = folder, Arguments = arguments.Length > 0 ? arguments : [$"tool-{id}"] };

    // Enough time passes for a new sample and report, in which the computer counts this much processor time.
    private async Task<ResourceReport> Read(long ticks = 400, long busy = 100)
    {
        time.Advance(TimeSpan.FromSeconds(2));
        totalTicks += ticks;
        busyTicks += busy;
        return await monitor.Read(CancellationToken.None);
    }

    private WorkspaceResources Of(ResourceReport report, WorkspaceInfo workspace) => report.Workspaces.Single(item => item.Id == workspace.Id);

    [Fact]
    public async Task Read_SaysOnlyThatItCannotMeasureWhereProcessesCannotBeRead()
    {
        source.SetupGet(x => x.IsSupported).Returns(false);

        ResourceReport report = await monitor.Read(CancellationToken.None);

        Assert.False(report.Supported);
        Assert.Null(report.Machine);
        source.Verify(x => x.List(), Times.Never);
    }

    [Fact]
    public async Task Read_GivesAProcessInAWorkspaceFolderAndEverythingItStartsToTheWorkspace()
    {
        Start(200, server, 300, "/work/orchard", "/usr/bin/claude", "--verbose");
        Start(201, 200, 100, "/tmp", "node");
        Start(202, 201, 50, "/work/harbor");
        Start(900, 1, 4000, "/work/orchard");

        ResourceReport report = await Read();

        WorkspaceResources found = Assert.Single(report.Workspaces);
        Assert.Equal((orchard.Id, "orchard", 450L, 3), (found.Id, found.Name, found.Total.Memory, found.Total.Processes));
        Assert.False(found.IsWorking);
        SessionResources main = Assert.Single(found.Sessions);
        Assert.Equal((orchard.Id, "Main"), (main.Id, main.Name));
        Assert.Equal(["claude --verbose", "node", "tool-202"], main.Usage.Heaviest.Select(process => process.Command).ToList());
        Assert.Equal((500L, 1), (report.Server.Memory, report.Server.Processes));
        Assert.Equal(0, report.Other.Processes);
    }

    [Fact]
    public async Task Read_CountsWhatBelongsToNoWorkspaceAsOtherAndAFolderThatOnlyStartsAlikeIsNotInTheWorkspace()
    {
        Start(200, server, 300, "/tmp");
        Start(201, server, 20, "/work/orchard-old");
        Start(202, server, 40, "/work/harbor/src/deep");

        ResourceReport report = await Read();

        Assert.Equal((320L, 2), (report.Other.Memory, report.Other.Processes));
        Assert.Equal(40, Of(report, harbor).Total.Memory);
        Assert.Single(report.Workspaces);
    }

    [Fact]
    public async Task Read_ListsAWorkingWorkspaceWithoutProcessesAndTheOneTakingTheMostFirst()
    {
        working.Add(orchard.Id);
        Start(200, server, 300, "/work/harbor");

        ResourceReport report = await Read();

        Assert.Equal([harbor.Id, orchard.Id], report.Workspaces.Select(item => item.Id).ToList());
        Assert.True(Of(report, orchard).IsWorking);
        Assert.Equal(0, Of(report, orchard).Total.Processes);
        Assert.False(Of(report, harbor).IsWorking);
    }

    [Fact]
    public async Task Read_ShowsSideSessionsUnderTheirWorkspaceAfterTheMainSessionAndKeepsTheInnermostFolder()
    {
        WorkspaceInfo second = AddSide(orchard, "second", "/work/orchard/.sessions/second");
        working.Add(second.Id);
        Start(200, server, 100, "/work/orchard");
        Start(201, server, 70, "/work/orchard/.sessions/second/src");

        ResourceReport report = await Read();

        WorkspaceResources found = Assert.Single(report.Workspaces);
        Assert.Equal(170, found.Total.Memory);
        Assert.True(found.IsWorking);
        Assert.Equal([("Main", 100L, false), ("second", 70L, true)], found.Sessions.Select(session => (session.Name, session.Usage.Memory, session.IsWorking)).ToList());
    }

    [Fact]
    public async Task Read_GivesAShellCommandToTheSessionThatRanItEvenWhereItRunsInAnotherFolder()
    {
        commands.Setup(x => x.Find("make")).Returns([harbor.Id]);
        Start(200, server, 100, "/work/orchard", "/bin/sh", "-c", "make");
        Start(201, 200, 60, "/work/orchard", "cc");

        ResourceReport report = await Read();

        Assert.Equal(160, Assert.Single(report.Workspaces).Total.Memory);
        Assert.Equal(harbor.Id, report.Workspaces[0].Id);
    }

    [Fact]
    public async Task Read_GivesACommandThatSeveralSessionsRanToTheOneWhoseFolderItRunsIn()
    {
        commands.Setup(x => x.Find("make")).Returns([harbor.Id, orchard.Id]);
        Start(200, server, 100, "/work/orchard", "/bin/sh", "-c", "make");
        Start(201, server, 10, "/tmp", "/bin/sh", "-c", "make");

        ResourceReport report = await Read();

        Assert.Equal(100, Of(report, orchard).Total.Memory);
        Assert.Equal(10, Of(report, harbor).Total.Memory);
    }

    [Fact]
    public async Task Read_CorrectsWhatTheFolderSaidOnceTheCommandIsKnown()
    {
        Start(200, server, 100, "/work/orchard", "/bin/sh", "-c", "make");
        Assert.Equal(orchard.Id, Assert.Single((await Read()).Workspaces).Id);
        commands.Setup(x => x.Find("make")).Returns([harbor.Id]);

        ResourceReport report = await Read();

        Assert.Equal(harbor.Id, Assert.Single(report.Workspaces).Id);
    }

    [Fact]
    public async Task Read_KeepsAProcessWithItsWorkspaceAfterWhatStartedItEndedAndItMovedElsewhere()
    {
        Start(200, server, 100, "/work/orchard");
        Start(201, 200, 60, "/work/orchard");
        await Read();
        processes.Remove(200);
        processes[201] = processes[201] with { Parent = 1, Folder = "/" };
        Start(202, 201, 5, "/");

        ResourceReport report = await Read();

        Assert.Equal((65L, 2), (Of(report, orchard).Total.Memory, Of(report, orchard).Total.Processes));
    }

    [Fact]
    public async Task Read_TellsAProcessApartFromAnEarlierOneThatHadItsIdentifier()
    {
        Start(200, server, 100, "/work/orchard");
        await Read();
        processes[200] = new FakeProcess { Parent = 1, Started = 999, Memory = 100, Folder = "/work/orchard", Arguments = ["editor"] };

        ResourceReport report = await Read();

        Assert.Empty(report.Workspaces);
    }

    [Fact]
    public async Task Read_FindsDetachedProcessesThroughTheServiceTheServerRunsAs()
    {
        Start(200, 1, 100, "/work/orchard");
        Start(201, 200, 10, "/");
        Start(202, 1, 30, "/");
        members.UnionWith([server, 200, 202]);

        ResourceReport report = await Read();

        Assert.Equal(110, Of(report, orchard).Total.Memory);
        Assert.Equal(30, report.Other.Memory);
        Assert.Equal(500, report.Server.Memory);
    }

    [Fact]
    public async Task Read_GivesTheProcessOfAScriptToTheWorkspaceThatRunsIt()
    {
        scriptProcesses.Add((harbor.Id, 200));
        Start(200, server, 100, "/work/orchard", "dotnet", "run");
        Start(201, 200, 20, "/work/orchard");

        ResourceReport report = await Read();

        WorkspaceResources found = Assert.Single(report.Workspaces);
        Assert.Equal((harbor.Id, 120L, true), (found.Id, found.Total.Memory, found.IsWorking));
    }

    [Fact]
    public async Task Read_LeavesTheProcessesOfARemovedSessionWithItsWorkspaceAndShowsTheNewNameOfARenamedOne()
    {
        WorkspaceInfo second = AddSide(orchard, "second", "/sides/second");
        WorkspaceInfo third = AddSide(orchard, "third", "/sides/third");
        Start(200, server, 100, "/sides/second");
        Start(201, server, 10, "/sides/third");
        await Read();
        sessions.Remove(second);
        sessions[sessions.IndexOf(third)] = third with { Side = third.Side! with { Name = "renamed" } };

        ResourceReport report = await Read();

        Assert.Equal(["renamed", "second (removed)"], Of(report, orchard).Sessions.Select(session => session.Name).ToList());
        sessions.Remove(orchard);
        Assert.Equal(110, (await Read()).Other.Memory);
    }

    [Fact]
    public async Task Read_MeasuresTheShareOfTheComputerUsedSinceTheSampleBefore()
    {
        Start(200, server, 100, "/work/orchard");
        processes[200] = processes[200] with { Ticks = 5000 };

        ResourceReport first = await Read();

        Assert.Null(first.Machine!.Cpu);
        Assert.Null(Of(first, orchard).Total.Cpu);
        processes[200] = processes[200] with { Ticks = 5100 };
        processes[server] = processes[server] with { Ticks = 20 };
        Start(201, 200, 10, "/work/orchard");
        processes[201] = processes[201] with { Ticks = 40 };

        ResourceReport second = await Read(ticks: 400, busy: 200);

        Assert.Equal(50, second.Machine!.Cpu);
        Assert.Equal(35, Of(second, orchard).Total.Cpu);
        Assert.Equal([25.0, 10.0], Of(second, orchard).Total.Heaviest.Select(process => process.Cpu!.Value).ToList());
        Assert.Equal(5, second.Server.Cpu);
        Assert.Equal((16000L, 8000L, 2000L, 1500L, 4), (second.Machine.TotalMemory, second.Machine.AvailableMemory, second.Machine.TotalSwap, second.Machine.FreeSwap, second.Machine.Cores));
    }

    [Fact]
    public async Task Read_ListsOnlyTheLargestProcessesAndShortensLongCommandLines()
    {
        for (int index = 0; index < 8; index++)
        {
            Start(200 + index, server, 10 + index, "/work/orchard", "tool", new string('x', 400), "second\nline");
        }

        ResourceReport report = await Read();

        ResourceUsage total = Of(report, orchard).Total;
        Assert.Equal((8, 108L), (total.Processes, total.Memory));
        Assert.Equal([17L, 16L, 15L, 14L, 13L], total.Heaviest.Select(process => process.Memory).ToList());
        Assert.Equal(201, total.Heaviest[0].Command.Length);
        Assert.EndsWith("…", total.Heaviest[0].Command);
    }

    [Fact]
    public async Task Read_ListsAProcessThatKeepsTheProcessorBusyHoweverLittleMemoryItTakes()
    {
        for (int index = 0; index < 6; index++)
        {
            Start(200 + index, server, 100 + index, "/work/orchard");
        }

        Start(300, server, 1, "/work/orchard", "spinning");
        await Read();
        processes[300] = processes[300] with { Ticks = 100 };

        ResourceReport report = await Read();

        Assert.Equal([105L, 104L, 103L, 102L, 101L, 1L], Of(report, orchard).Total.Heaviest.Select(process => process.Memory).ToList());
        Assert.Equal(25, Of(report, orchard).Total.Heaviest[^1].Cpu);
    }

    [Fact]
    public async Task Read_LeavesOutAProcessThatEndedSinceItWasSampled()
    {
        Start(200, server, 100, "/work/orchard");
        Start(201, server, 0, "/work/harbor");

        ResourceReport report = await Read();

        Assert.Equal(orchard.Id, Assert.Single(report.Workspaces).Id);
    }

    [Fact]
    public async Task Read_AnswersWithTheSameReportForAShortWhileSoSeveralClientsCostNoMore()
    {
        ResourceReport first = await Read();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(first, await monitor.Read(CancellationToken.None));
        Assert.NotSame(first, await Read());
        source.Verify(x => x.List(), Times.Exactly(2));
    }

    [Fact]
    public async Task Open_FollowsTheProcessesWhileWorkRunsSoWhatDetachesLaterIsStillKnown()
    {
        using CancellationTokenSource stopping = new();
        working.Add(orchard.Id);
        Start(200, server, 100, "/work/orchard");
        Start(201, 200, 60, "/work/orchard");

        monitor.Open(stopping.Token);
        await Until(() => source.Invocations.Count(call => call.Method.Name == nameof(IProcessSource.Folder)) > 0);
        working.Clear();
        processes.Remove(200);
        processes[201] = processes[201] with { Parent = 1, Folder = "/" };

        ResourceReport report = await Read();

        Assert.Equal(60, Of(report, orchard).Total.Memory);
        await stopping.CancelAsync();
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed record FakeProcess
    {
        public required int Parent { get; init; }

        public long Started { get; init; } = 1;

        public long Ticks { get; init; }

        public required long Memory { get; init; }

        public required IReadOnlyList<string> Arguments { get; init; }

        public required string Folder { get; init; }
    }

    // Only what is asked of the time is simulated; waits take real time.
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan time) => now += time;
    }
}
