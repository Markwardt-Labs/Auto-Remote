namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class AgendaServiceTests
{
    public AgendaServiceTests()
    {
        followUps.Setup(x => x.Describe(workspace.Id)).Returns(() => state);
        savedInputs.Setup(x => x.HasQueued(workspace.Id)).Returns(() => inputs.Any(input => input.IsQueued));
        savedInputs.Setup(x => x.List(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => inputs);
        loops.Setup(x => x.Get(workspace.Id)).Returns(() => loop);
        queue.Setup(x => x.Get(workspace.Id)).Returns(() => local);
        workspaces.Setup(x => x.All(It.IsAny<CancellationToken>())).ReturnsAsync([workspace]);
        events.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            lock (published)
            {
                published.Add(remoteEvent);
            }
        });
        agenda = new AgendaService(workspaces.Object, followUps.Object, savedInputs.Object, loops.Object, queue.Object, Mock.Of<IWindowGuard>(), new WorkspaceActivity(events.Object), events.Object, _ => bringIns, TimeSpan.FromMilliseconds(10));
    }

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IFollowUps> followUps = new();

    private readonly Mock<ISavedInputs> savedInputs = new();

    private readonly Mock<IWorkspaceLoops> loops = new();

    private readonly Mock<IWorkQueue> queue = new();

    private readonly Mock<IEventHub> events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly AgendaService agenda;

    private FollowState state = new(false, false, false, false, null, null, null, null, null, null);

    private IReadOnlyList<SavedInput> inputs = [];

    private IReadOnlyList<BringInItem> bringIns = [];

    private LoopInfo? loop;

    private QueueStatus? local;

    private SavedInput Queued(string text) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = true };

    private LoopInfo Loop() => new() { Settings = new LoopSettings { Prompt = "review" }, StartedAt = DateTimeOffset.UtcNow, NextRunAt = DateTimeOffset.UtcNow.AddHours(1) };

    private Task<Agenda> Get() => agenda.Get(workspace.Id, CancellationToken.None);

    [Fact]
    public async Task NothingUpNext_HasNothingToSay()
    {
        Agenda current = await Get();

        Assert.Empty(current.Items);
        Assert.Null(current.Status);
        Assert.False(current.IsPending);
    }

    [Fact]
    public async Task Items_ComeInTheOrderTheyRun()
    {
        state = state with { IsCommitDue = true, Retry = new WorkStep { Source = ActivitySource.Queue, Text = "again" }, RetryAt = DateTimeOffset.UtcNow.AddMinutes(4), RetryReason = "usage limit reached", IsAwaited = true };
        bringIns = [new BringInItem(Guid.NewGuid(), "Bring in docs", true)];
        inputs = [Queued("first\nmore"), Queued("second"), new SavedInput { Id = Guid.NewGuid(), Text = "a draft", Time = DateTimeOffset.UtcNow }];
        loop = Loop();

        Agenda current = await Get();

        Assert.Equal([AgendaKind.Commit, AgendaKind.Retry, AgendaKind.BringIn, AgendaKind.DelegatedResults, AgendaKind.Queued, AgendaKind.Queued, AgendaKind.Loop], current.Items.Select(item => item.Kind).ToList());
        Assert.Equal("first", current.Items[4].Title);
        Assert.True(current.IsPending);
        Assert.Equal("Retrying in 4 min: usage limit reached · 4 up next", current.Status);
    }

    [Fact]
    public async Task AStoppedChat_SaysSoAndHasNothingToWaitFor()
    {
        state = state with { IsStopped = true };
        inputs = [Queued("next")];

        Agenda current = await Get();

        Assert.Equal("Stopped · 1 up next", current.Status);
        Assert.True(current.IsStopped);
        Assert.False(current.IsPending);
    }

    [Theory]
    [InlineData(true, true, "Waiting for delegated tasks · 1 up next")]
    [InlineData(true, false, null)]
    [InlineData(false, true, "1 up next")]
    public async Task TheStatus_SaysWhatTheQueueWaitsFor(bool awaited, bool queuedInput, string? expected)
    {
        state = state with { IsAwaited = awaited };
        inputs = queuedInput ? [Queued("next")] : [];

        Assert.Equal(expected, (await Get()).Status);
    }

    [Fact]
    public async Task WorkWaitingForTheLocalAi_IsSaidFirst()
    {
        local = new QueueStatus { Position = 2, Text = "write" };

        Agenda current = await Get();

        Assert.Equal("Waiting for Local AI, position 2", current.Status);
        Assert.Equal(AgendaKind.LocalAi, Assert.Single(current.Items).Kind);
    }

    [Fact]
    public async Task ALoopBetweenItsRuns_ShowsNoStatusAndDoesNotCountAsPending()
    {
        loop = Loop();

        Agenda current = await Get();

        Assert.Null(current.Status);
        Assert.False(current.IsPending);
        Assert.Equal("Loop: review", Assert.Single(current.Items).Title);
    }

    [Fact]
    public async Task Open_TellsClientsOnlyWhatChanges()
    {
        using CancellationTokenSource stopping = new();
        agenda.Open(stopping.Token);
        await Task.Delay(80);

        lock (published)
        {
            Assert.DoesNotContain(published, item => item.Kind == RemoteEventKind.AgendaChanged);
        }

        inputs = [Queued("next")];
        await Task.Delay(150);
        inputs = [];
        await Task.Delay(150);
        await stopping.CancelAsync();

        lock (published)
        {
            Assert.Equal([1, 0], published.Where(item => item.Kind == RemoteEventKind.AgendaChanged).Select(item => item.Agenda!.Items.Count).ToList());
        }
    }
}
