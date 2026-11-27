namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ResourcesViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid orchard = Guid.NewGuid();

    private readonly Guid harbor = Guid.NewGuid();

    private readonly long mebibyte = 1024 * 1024;

    private ResourceReport Sample(long available = 8000, bool sessions = true)
    {
        ResourceUsage main = Usage(1536, 25, 2, new ProcessUsage { Id = 1, Command = "claude", Memory = 1000 * mebibyte, Cpu = 0.2 });
        ResourceUsage side = Usage(200, null, 1);
        List<SessionResources> parts = [new SessionResources { Id = orchard, Name = "Main", IsWorking = true, Usage = main }];

        if (sessions)
        {
            parts.Add(new SessionResources { Id = Guid.NewGuid(), Name = "second", Usage = side });
        }

        return new ResourceReport
        {
            Supported = true,
            Machine = new MachineResources { TotalMemory = 16384 * mebibyte, AvailableMemory = available * mebibyte, TotalSwap = 2048 * mebibyte, FreeSwap = 1536 * mebibyte, Cores = 8, Cpu = 42.4 },
            Workspaces =
            [
                new WorkspaceResources { Id = orchard, Name = "orchard", IsWorking = true, Total = Usage(1736, 25, 3), Sessions = parts },
                new WorkspaceResources { Id = harbor, Name = "harbor", Total = Usage(64, 0, 1), Sessions = [new SessionResources { Id = harbor, Name = "Main", Usage = Usage(64, 0, 1) }] }
            ],
            Server = Usage(300, 1, 1)
        };
    }

    private ResourceUsage Usage(long megabytes, double? cpu, int processes, params ProcessUsage[] largest) => new() { Memory = megabytes * mebibyte, Cpu = cpu, Processes = processes, Heaviest = largest };

    private ResourcesViewModel Open(ResourceReport report, Guid? workspace = null)
    {
        connection.Setup(x => x.GetResources(It.IsAny<CancellationToken>())).ReturnsAsync(report);
        return new ResourcesViewModel(connection.Object, TimeSpan.FromHours(1)) { Workspace = workspace, IsExpanded = true };
    }

    private string[] Rows(ResourcesViewModel model) => [.. model.Rows.Select(row => $"{(row.IsPart ? "> " : string.Empty)}{row.Name} | {row.State} | {row.Summary}")];

    [Fact]
    public void Rows_ListEveryWorkspaceWithItsSessionsWhenThereAreSeveralAndThenTheServer()
    {
        using ResourcesViewModel model = Open(Sample());

        Assert.Equal(
        [
            "orchard | Working | 1.7 GB · 25% CPU · 3 processes",
            "> Main | Working | 1.5 GB · 25% CPU · 2 processes",
            "> second | Not working | 200 MB · 1 process",
            "harbor | Not working | 64 MB · 0% CPU · 1 process",
            "Server itself |  | 300 MB · 1% CPU · 1 process"
        ], Rows(model));
        Assert.Null(model.Notice);
        Assert.Null(model.Warning);
        Assert.Equal(["Server computer memory: 8.2 GB of 16.0 GB used, 7.8 GB available", "Swap: 512 MB of 2.0 GB used", "Processor: 42% of 8 cores"], model.MachineLines);
    }

    [Fact]
    public void Rows_OfAWorkspaceListItsSessionsWithTheProcessesThatTakeTheMost()
    {
        using ResourcesViewModel model = Open(Sample(), orchard);

        Assert.Equal(["All sessions |  | 1.7 GB · 25% CPU · 3 processes", "> Main | Working | 1.5 GB · 25% CPU · 2 processes", "> second | Not working | 200 MB · 1 process"], Rows(model));
        ResourceProcessLine process = Assert.Single(model.Rows[1].Processes);
        Assert.Equal(("claude", "1000 MB · <1% CPU"), (process.Command, process.Usage));
    }

    [Fact]
    public void Rows_OfAWorkspaceWithOnlyItsMainSessionNeedNoTotal()
    {
        using ResourcesViewModel model = Open(Sample(sessions: false), orchard);

        Assert.Equal(["Main | Working | 1.5 GB · 25% CPU · 2 processes"], Rows(model));
    }

    [Fact]
    public void Warning_SaysWhenLittleMemoryIsLeft()
    {
        using ResourcesViewModel model = Open(Sample(available: 1600));

        Assert.True(model.HasWarning);
        Assert.StartsWith("The server computer is low on memory.", model.Warning);
    }

    [Fact]
    public void Notice_SaysWhyThereIsNothingToList()
    {
        using ResourcesViewModel elsewhere = Open(Sample(), Guid.NewGuid());
        Assert.Equal("Nothing is running in this workspace.", elsewhere.Notice);
        Assert.Empty(elsewhere.Rows);

        using ResourcesViewModel unsupported = Open(new ResourceReport { Supported = false });
        Assert.Equal("This server can only measure resources on Linux.", unsupported.Notice);
        Assert.Empty(unsupported.Rows);
        Assert.Empty(unsupported.MachineLines);

        using ResourcesViewModel quiet = Open(new ResourceReport { Supported = true });
        Assert.Equal("No workspace is working.", quiet.Notice);
    }

    [Fact]
    public async Task Reading_GoesOnThroughFailuresWhileWatchedAndStopsOnceNothingShowsIt()
    {
        connection.SetupSequence(x => x.GetResources(It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The connection was lost.")).ReturnsAsync(Sample()).ReturnsAsync(Sample()).ReturnsAsync(Sample());
        using ResourcesViewModel model = new(connection.Object, TimeSpan.FromMilliseconds(10));
        List<string?> notices = [];
        model.Updated += () => notices.Add(model.Notice);
        Assert.Equal("Reading…", model.Notice);
        connection.Verify(x => x.GetResources(It.IsAny<CancellationToken>()), Times.Never);

        model.IsListed = true;

        for (int attempt = 0; attempt < 500 && model.Rows.Count == 0; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Resources could not be read: The connection was lost.", notices[0]);
        Assert.NotEmpty(model.Rows);
        Assert.Equal("1.7 GB · 25% CPU", model.Brief(orchard, false));
        Assert.Equal(string.Empty, model.Brief(harbor, false));
        Assert.Equal("64 MB · 0% CPU", model.Brief(harbor, true));

        model.IsOnScreen = false;
        await Task.Delay(50);
        int reads = connection.Invocations.Count;
        await Task.Delay(60);

        Assert.Equal(reads, connection.Invocations.Count);
        Assert.Empty(model.Rows);
        Assert.Equal(string.Empty, model.Brief(orchard, true));
    }
}
