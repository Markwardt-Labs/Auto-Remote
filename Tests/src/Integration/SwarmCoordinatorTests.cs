namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class SwarmCoordinatorTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly Mock<ISwarmRepository> repository = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkQueue> queue = new();

    private readonly EventHub events = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly SwarmIdentity worker = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    private readonly TaskCompletionSource<bool> reportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<WorkSource> submitted = [];

    private readonly WorkspaceActivity activity;

    private readonly SwarmBook book;

    private readonly SwarmCoordinator coordinator;

    public SwarmCoordinatorTests()
    {
        activity = new WorkspaceActivity(events);
        book = new SwarmBook(events, new ISwarmIdentity.Factory());
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        repository.Setup(x => x.Snapshot(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((new string('a', 40), "project"));
        repository.Setup(x => x.Bundle(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(ReadOnlyMemory<byte>.Empty);
        queue.Setup(x => x.Submit(workspace.Id, It.IsAny<string>(), It.IsAny<WorkSource>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string _, WorkSource source, CancellationToken _) =>
        {
            lock (submitted)
            {
                submitted.Add(source);
            }

            return new WorkTicket(true, reportStarted.Task);
        });
        coordinator = new SwarmCoordinator(book, new SwarmCourier(book, new SwarmSealer()), repository.Object, workspaces.Object, queue.Object, activity) { ReportRetry = TimeSpan.FromMilliseconds(20) };
    }

    public async Task InitializeAsync()
    {
        await book.Open(temp.Path, CancellationToken.None);
        await book.Change(document => document.Peers.Add(new SwarmPeerRecord
        {
            Identity = worker.Public,
            Name = "box",
            SendWork = true,
            Mode = SwarmMode.Direct,
            Profile = new SwarmProfileBody { Name = "box", AcceptsWork = true, FreeSlots = 1, Capabilities = [new SwarmCapability { Capability = AiCapability.Balanced, Model = "m" }] },
            LastSeen = DateTimeOffset.UtcNow
        }));
    }

    // What the coordinator sends goes on after a test ends, so the folder may be written to once more while it is removed.
    public async Task DisposeAsync()
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                temp.Dispose();
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(20);
            }
        }
    }

    private string Peer => worker.Public.Id;

    private SwarmTaskRequest Request() => new() { Title = "t", Instructions = "i", Capability = AiCapability.Balanced };

    private async Task<Guid> Delegate(bool manual = true) => (await coordinator.Delegate(workspace.Id, [new SwarmPlannedTask(Request(), [])], manual, CancellationToken.None))[0];

    private Task<SwarmTaskState> State(Guid id) => book.Read(document => document.Outgoing.Single(task => task.Id == id).State);

    private Task Heard(DateTimeOffset when) => book.Change(document => document.Peers[0].LastSeen = when);

    private async Task Until(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 400 && !await condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(await condition());
    }

    [Fact]
    public async Task APeerRevokedWhileATaskIsBeingPackedForItDoesNotKeepTheTask()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> packing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(x => x.Bundle(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).Returns(packing.Task);
        Task<Guid> delegating = Delegate();
        await Until(() => Task.FromResult(repository.Invocations.Any(call => call.Method.Name == nameof(ISwarmRepository.Bundle))));
        await book.Change(document => document.Peers[0].SendWork = false);

        Task revoking = coordinator.Revoke(Peer);
        await Task.Delay(50);
        packing.SetResult(ReadOnlyMemory<byte>.Empty);
        await revoking;

        Assert.Equal(SwarmTaskState.Failed, await State(await delegating));
    }

    [Fact]
    public async Task ATaskCancelledWhileItIsPreparedAfterItsDependencyStaysCancelled()
    {
        IReadOnlyList<Guid> tasks = await coordinator.Delegate(workspace.Id, [new SwarmPlannedTask(Request(), []), new SwarmPlannedTask(Request(), [0])], true, CancellationToken.None);
        TaskCompletionSource pinning = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(x => x.Pin(It.IsAny<string>(), tasks[1], It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(pinning.Task);
        await book.Change(document => document.Outgoing.Single(task => task.Id == tasks[0]).State = SwarmTaskState.Done);
        Task dispatching = coordinator.Dispatch();
        await Until(() => Task.FromResult(repository.Invocations.Any(call => call.Method.Name == nameof(ISwarmRepository.Pin))));

        Task cancelling = coordinator.Cancel(tasks[1]);
        await Until(async () => await State(tasks[1]) == SwarmTaskState.Cancelled);
        pinning.SetResult();
        await dispatching;
        await cancelling;

        Assert.Equal(SwarmTaskState.Cancelled, await State(tasks[1]));
    }

    [Fact]
    public async Task APackageThatWaitsForItsAcknowledgmentIsNotSentAgainRightAwayOverTheSameConnectionButIsOnADrive()
    {
        await Delegate();
        Mock<ISwarmWorker> side = new();
        side.Setup(x => x.Profile(Peer)).ReturnsAsync(new SwarmProfileBody { Name = "here" });
        SwarmExchange exchange = new(book, new SwarmCourier(book, new SwarmSealer()), side.Object, coordinator);
        SwarmCursor cursor = new();
        int Kept(IReadOnlyList<SwarmParcel> mail) => mail.Count(parcel => parcel.Name is not ("profile.msg" or "ack.msg" or "progress.msg"));

        Assert.Equal(1, Kept(await exchange.Mail(Peer, cursor)));
        Assert.Equal(0, Kept(await exchange.Mail(Peer, cursor)));
        Assert.Equal(1, Kept(await exchange.Mail(Peer, new SwarmCursor())));
        Assert.Equal(1, Kept(await exchange.Mail(Peer, null)));
        Assert.Equal(1, Kept(await exchange.Mail(Peer, null)));
    }

    [Fact]
    public async Task ATaskWithoutAnyConversationCanBeDiscarded()
    {
        Guid task = await Delegate();
        await coordinator.Cancel(task);

        await coordinator.Discard(task, CancellationToken.None);

        Assert.Empty(await book.Read(document => document.Outgoing.ToList()));
    }

    [Fact]
    public async Task ATaskWhoseWorkerStoppedReceivingWhileItWasSentFailsRatherThanWaitingUnderAnIdentifierTheWorkerWasToldToDrop()
    {
        TaskCompletionSource<ReadOnlyMemory<byte>> packing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.Setup(x => x.Bundle(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).Returns(packing.Task);
        Task<Guid> delegating = Delegate();
        await Until(() => Task.FromResult(repository.Invocations.Any(call => call.Method.Name == nameof(ISwarmRepository.Bundle))));
        await book.Change(document => document.Peers[0].SendWork = false);
        packing.SetResult(ReadOnlyMemory<byte>.Empty);
        Guid task = await delegating;

        await Until(async () => await State(task) == SwarmTaskState.Failed);
    }

    [Fact]
    public async Task ATaskTheWorkerNeverAcknowledgedIsLostLikeAnyOtherAndComesBackWhenItDoes()
    {
        Guid task = await Delegate();
        Guid message = (await book.Read(document => document.Outgoing.Single().MessageId))!.Value;
        await coordinator.Open(CancellationToken.None);
        await book.Change(document => document.Outgoing.Single().UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1));
        await Heard(DateTimeOffset.UtcNow.AddHours(-1));
        await Task.Delay(30);

        await coordinator.CheckLost(TimeSpan.FromMilliseconds(10));

        Assert.Equal(SwarmTaskState.Lost, await State(task));

        await coordinator.Delivered(Peer, message);

        Assert.Equal(SwarmTaskState.Queued, await State(task));
    }

    [Fact]
    public async Task ATaskWaitsWhileTheWorkerThatSaidItWasFreeHasNotBeenHeardFromLately()
    {
        await Heard(DateTimeOffset.UtcNow.AddMinutes(-5));

        Guid task = await Delegate();

        Assert.Equal(SwarmTaskState.Pending, await State(task));

        await Heard(DateTimeOffset.UtcNow);
        await coordinator.Dispatch();

        Assert.Equal(SwarmTaskState.InTransit, await State(task));
    }

    [Fact]
    public async Task ARestartedServerDoesNotCountTheTimeItWasNotListeningAsSilenceOfAWorker()
    {
        Guid task = await Delegate();
        await book.Change(document =>
        {
            SwarmOutgoing entry = document.Outgoing.Single();
            entry.State = SwarmTaskState.Running;
            entry.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        });
        await Heard(DateTimeOffset.UtcNow.AddHours(-1));
        await coordinator.Open(CancellationToken.None);

        await coordinator.CheckLost(TimeSpan.FromMinutes(3));

        Assert.Equal(SwarmTaskState.Running, await State(task));

        await Task.Delay(30);
        await coordinator.CheckLost(TimeSpan.FromMilliseconds(10));

        Assert.Equal(SwarmTaskState.Lost, await State(task));
    }

    [Fact]
    public async Task AResultThatCannotBeTakenForNowIsAskedForAgainAndOnlyAnUnusableOneFailsTheTask()
    {
        Guid task = await Delegate();
        SwarmResultBody result = new() { TaskId = task, Outcome = SwarmOutcome.Done, ResultCommit = new string('b', 40) };
        bool temporary = true;
        repository.Setup(x => x.Import(It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), task, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(() => Task.FromException<(string, string)>(new RequestException("git failed") { IsTemporary = temporary }));

        RequestException again = await Assert.ThrowsAsync<RequestException>(() => coordinator.Receive(Peer, result, ReadOnlyMemory<byte>.Empty));

        Assert.True(again.IsTemporary);
        Assert.Equal(SwarmTaskState.InTransit, await State(task));

        temporary = false;
        await coordinator.Receive(Peer, result, ReadOnlyMemory<byte>.Empty);

        Assert.Equal(SwarmTaskState.Failed, await State(task));
    }

    [Fact]
    public async Task AJobCountsAsReportedOnlyOnceItsMessageStartedAndIsNotGivenWhileGitHasTheWorkspace()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        Guid task = await Delegate(false);
        await coordinator.Receive(Peer, new SwarmResultBody { TaskId = task, Outcome = SwarmOutcome.Failed, Error = "no" }, ReadOnlyMemory<byte>.Empty);
        await Task.Delay(150);

        Assert.Empty(submitted);

        activity.Leave(workspace.Id, WorkspaceState.Managing);
        await Until(() => Task.FromResult(submitted.Count == 1));
        await Task.Delay(100);

        Assert.Equal([WorkSource.Report], submitted);
        Assert.False(await book.Read(document => document.Jobs.Single().IsReported));

        reportStarted.SetResult(true);

        await Until(() => book.Read(document => document.Jobs.Single().IsReported));
    }
}
