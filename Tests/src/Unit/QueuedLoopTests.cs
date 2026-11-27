namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class QueuedLoopTests
{
    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IWorkQueue> queue = new();

    private readonly EventHub events = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Local };

    private readonly ConcurrentQueue<string?> ends = new();

    private readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly WorkspaceLoops loops;

    private int submitted;

    public QueuedLoopTests()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        queue.Setup(x => x.Submit(workspace.Id, "again", WorkSource.Loop, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Interlocked.Increment(ref submitted);
            return new WorkTicket(true, started.Task);
        });
        queue.Setup(x => x.CancelLoop(workspace.Id)).Returns(Task.CompletedTask);
        events.Subscribe(remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.LoopChanged && remoteEvent.Loop is null)
            {
                ends.Enqueue(remoteEvent.Text);
            }
        });
        loops = new WorkspaceLoops(workspaces.Object, prompts.Object, new WorkspaceActivity(events), Mock.Of<IUsageService>(), events, TimeSpan.FromMilliseconds(10), queue: queue.Object);
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private Task Start() => loops.Start(workspace.Id, new LoopSettings { Prompt = "again", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);

    [Fact]
    public async Task AQueuedRunCountsOnlyOnceItStartsAndNoOtherRunIsSubmittedWhileItWaits()
    {
        await Start();
        await Until(() => submitted == 1);
        await Task.Delay(100);

        Assert.Equal((1, 0), (submitted, loops.Get(workspace.Id)?.Runs));

        started.SetResult(true);
        await Until(() => loops.Get(workspace.Id) is null && !ends.IsEmpty);

        Assert.Equal(1, submitted);
        Assert.Equal("The loop made all its runs.", Assert.Single(ends));
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancellingTheWaitingRunEndsTheLoop()
    {
        await Start();
        await Until(() => submitted == 1);

        started.SetResult(false);
        await Until(() => loops.Get(workspace.Id) is null && !ends.IsEmpty);

        Assert.Equal("The loop stopped: its waiting run was cancelled.", Assert.Single(ends));
    }

    [Fact]
    public async Task CancellingTheLoopRemovesItsWaitingRunFromTheQueue()
    {
        await Start();
        await Until(() => submitted == 1);

        await loops.Cancel(workspace.Id);

        Assert.Null(loops.Get(workspace.Id));
        queue.Verify(x => x.CancelLoop(workspace.Id), Times.Once);
    }
}
