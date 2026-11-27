namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkQueueTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IPrompterHost> host = new();

    private readonly Mock<ISavedInputs> savedInputs = new();

    private readonly EventHub events = new();

    private readonly WorkspaceActivity activity;

    private readonly ConcurrentDictionary<Guid, bool> busy = new();

    private readonly List<(Guid Id, string Text)> sent = [];

    private readonly List<RemoteEvent> published = [];

    private readonly Dictionary<Guid, WorkspaceInfo> known = [];

    private readonly WorkQueue queue;

    private AiEnvironment environment = new();

    private string? refusal;

    private bool halted;

    public WorkQueueTests()
    {
        activity = new WorkspaceActivity(events);
        events.Subscribe(remoteEvent =>
        {
            lock (published)
            {
                published.Add(remoteEvent);
            }
        });
        host.Setup(x => x.Get()).Returns(() => environment);
        prompts.Setup(x => x.IsBusy(It.IsAny<Guid>())).Returns((Guid id) => busy.ContainsKey(id));
        prompts.Setup(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns((Guid id, string text, bool _, CancellationToken _) => Send(id, text));
        prompts.Setup(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((Guid id, string text, CancellationToken _) => Send(id, text));
        queue = new WorkQueue(workspaces.Object, prompts.Object, host.Object, activity, savedInputs.Object, events);
    }

    public void Dispose() => temp.Dispose();

    private Task Send(Guid id, string text)
    {
        if (refusal is { } reason)
        {
            return Task.FromException(new RequestException(reason) { IsHalted = halted });
        }

        lock (sent)
        {
            sent.Add((id, text));
        }

        busy[id] = true;
        activity.TryEnter(id, WorkspaceState.Working);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = true });
        return Task.CompletedTask;
    }

    private void Finish(Guid id)
    {
        busy.TryRemove(id, out _);
        activity.Leave(id, WorkspaceState.Working);
        events.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = false });
    }

    private Guid Workspace(string name, AiService service = AiService.Local)
    {
        WorkspaceInfo info = new() { Id = Guid.NewGuid(), Folder = $"/work/{name}", Service = service };
        known[info.Id] = info;
        workspaces.Setup(x => x.Get(info.Id, It.IsAny<CancellationToken>())).ReturnsAsync(info);
        return info.Id;
    }

    private List<string> Sent()
    {
        lock (sent)
        {
            return [.. sent.Select(item => item.Text)];
        }
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private Task<WorkTicket> Submit(Guid id, string text, WorkSource source = WorkSource.Direct) => queue.Submit(id, text, source, CancellationToken.None);

    [Fact]
    public async Task Yield_QueuesWorkOfAnotherWorkspaceUntilTheRunningWorkHasFinished()
    {
        Guid first = Workspace("one");
        Guid second = Workspace("two");

        WorkTicket running = await Submit(first, "a");
        WorkTicket waiting = await Submit(second, "b");

        Assert.False(running.Queued);
        Assert.True(waiting.Queued);
        Assert.Equal(["a"], Sent());
        QueueStatus status = queue.Get(second)!;
        Assert.Equal((1, "b", false), (status.Position, status.Text, status.Delegated));
        Assert.Equal(["one"], status.Running);
        Assert.Equal([second], queue.Waiting);
        Assert.False(waiting.Started.IsCompleted);

        Finish(first);

        Assert.True(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["a", "b"], Sent());
        Assert.Null(queue.Get(second));
        lock (published)
        {
            Assert.Null(published.Last(remoteEvent => remoteEvent.Kind == RemoteEventKind.QueueChanged && remoteEvent.WorkspaceId == second).Queue);
        }
    }

    [Fact]
    public async Task Yield_SendsAMessageForTheRunningWorkspaceIntoItsWorkInsteadOfQueuingIt()
    {
        Guid first = Workspace("one");
        await Submit(first, "a");

        WorkTicket steered = await Submit(first, "also this");

        Assert.False(steered.Queued);
        Assert.Equal(["a", "also this"], Sent());
        Assert.Null(queue.Get(first));
    }

    [Fact]
    public async Task Yield_JoinsFurtherMessagesIntoTheOneQueuedItemWithTheAttachmentLinesAtItsEnd()
    {
        Guid first = Workspace("one");
        Guid second = Workspace("two");
        await Submit(first, "a");
        await Submit(second, "look\nAttached file: /data/1.png");

        WorkTicket joined = await Submit(second, "and this\nAttached file: /data/2.txt");

        Assert.True(joined.Queued);
        Assert.Equal("look\n\nand this\nAttached file: /data/1.png\nAttached file: /data/2.txt", queue.Get(second)!.Text);
        Assert.Equal([second], queue.Waiting);
    }

    [Fact]
    public async Task Yield_StartsDirectWorkInOrderBeforeDelegatedWorkThatWasQueuedEarlier()
    {
        Guid running = Workspace("running");
        Guid delegated = Workspace("delegated");
        Guid early = Workspace("early");
        Guid late = Workspace("late");
        await Submit(running, "running");
        await Submit(delegated, "delegated", WorkSource.Delegated);
        await Submit(early, "early");
        await Submit(late, "late");

        Assert.Equal((3, 1, 2), (queue.Get(delegated)!.Position, queue.Get(early)!.Position, queue.Get(late)!.Position));

        Finish(running);
        await Until(() => Sent().Count == 2);
        Finish(early);
        await Until(() => Sent().Count == 3);
        Finish(late);
        await Until(() => Sent().Count == 4);

        Assert.Equal(["running", "early", "late", "delegated"], Sent());
    }

    [Fact]
    public async Task Yield_NeverInterruptsRunningDelegatedWorkAndTakesNoMoreDelegatedWorkWhileAnythingRunsOrWaits()
    {
        Assert.Equal(1, queue.FreeDelegatedSlots);
        Guid delegated = Workspace("delegated");
        Guid direct = Workspace("direct");
        await Submit(delegated, "task", WorkSource.Delegated);

        Assert.Equal(0, queue.FreeDelegatedSlots);
        WorkTicket mine = await Submit(direct, "mine");

        Assert.True(mine.Queued);
        Assert.Equal(["task"], Sent());

        Finish(delegated);
        await Until(() => Sent().Count == 2);

        Assert.Equal(0, queue.FreeDelegatedSlots);
        Finish(direct);
        await Until(() => queue.FreeDelegatedSlots == 1);
    }

    [Fact]
    public async Task Simultaneous_RunsWorkSideBySideAndLimitsOnlyTheDelegatedTasks()
    {
        environment = new AiEnvironment { LocalWorkMode = LocalWorkMode.Simultaneous, DelegatedTaskLimit = 1 };
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        Guid first = Workspace("first");
        Guid next = Workspace("next");

        await Submit(one, "a");
        await Submit(two, "b");
        Assert.Equal(1, queue.FreeDelegatedSlots);
        WorkTicket started = await Submit(first, "t1", WorkSource.Delegated);
        WorkTicket waiting = await Submit(next, "t2", WorkSource.Delegated);

        Assert.False(started.Queued);
        Assert.True(waiting.Queued);
        Assert.Equal(["a", "b", "t1"], Sent());
        Assert.Equal(0, queue.FreeDelegatedSlots);

        Finish(first);

        Assert.True(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SwitchingToSimultaneousStartsWhatWaitsAndSwitchingBackLetsRunningWorkFinishFirst()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        Guid three = Workspace("three");
        await Submit(one, "a");
        WorkTicket waiting = await Submit(two, "b");

        environment = new AiEnvironment { LocalWorkMode = LocalWorkMode.Simultaneous };
        queue.Pump();

        Assert.True(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        environment = new AiEnvironment();
        WorkTicket later = await Submit(three, "c");
        Finish(one);
        await Task.Delay(100);

        Assert.True(later.Queued);
        Assert.False(later.Started.IsCompleted);
        Finish(two);
        Assert.True(await later.Started.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QueuedWork_WaitsForAGitActionOfItsWorkspaceAndLetsTheWorkBehindItGoFirst()
    {
        Guid running = Workspace("running");
        Guid managed = Workspace("managed");
        Guid other = Workspace("other");
        await Submit(running, "a");
        await Submit(managed, "b");
        await Submit(other, "c");
        activity.TryEnter(managed, WorkspaceState.Managing);

        Finish(running);
        await Until(() => Sent().Count == 2);
        Assert.Equal(["a", "c"], Sent());
        Finish(other);
        await Task.Delay(100);
        Assert.Equal(2, Sent().Count);

        activity.Leave(managed, WorkspaceState.Managing);

        await Until(() => Sent().Count == 3);
    }

    [Theory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task OtherProviders_AreNeverQueuedAndDoNotHoldTheLocalAi(AiService service)
    {
        Guid local = Workspace("local");
        Guid cloud = Workspace("cloud", service);
        await Submit(local, "a");

        WorkTicket ticket = await Submit(cloud, "b");

        Assert.False(ticket.Queued);
        Assert.Equal(["a", "b"], Sent());
    }

    [Fact]
    public async Task Cancel_RemovesTheQueuedPromptAnswersItsTextAndNeverStartsIt()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        await Submit(one, "a");
        WorkTicket waiting = await Submit(two, "b");

        QueueStatus? removed = await queue.Cancel(two);

        Assert.Equal("b", removed?.Text);
        Assert.False(await waiting.Started);
        Assert.Null(queue.Get(two));
        Assert.Null(await queue.Cancel(two));
        Finish(one);
        await Task.Delay(100);
        Assert.Equal(["a"], Sent());
        Assert.True(busy.IsEmpty);
    }

    [Fact]
    public async Task CancelLoop_RemovesOnlyAWaitingLoopRunThatNoMessageJoined()
    {
        Guid one = Workspace("one");
        Guid looping = Workspace("looping");
        Guid joined = Workspace("joined");
        await Submit(one, "a");
        WorkTicket run = await Submit(looping, "loop", WorkSource.Loop);
        await Submit(joined, "loop", WorkSource.Loop);
        await Submit(joined, "mine");
        await Assert.ThrowsAsync<RequestException>(() => Submit(looping, "loop", WorkSource.Loop));

        await queue.CancelLoop(looping);
        await queue.CancelLoop(joined);

        Assert.False(await run.Started);
        Assert.Equal("loop\n\nmine", queue.Get(joined)?.Text);
        Assert.Null(queue.Get(looping));
    }

    [Fact]
    public async Task Withdraw_TakesOnlyTheGivenTextOutOfWhatWaitsAndDropsWhatHoldsNothingElse()
    {
        Guid one = Workspace("one");
        Guid alone = Workspace("alone");
        Guid joined = Workspace("joined");
        await Submit(one, "a");
        WorkTicket only = await Submit(alone, "request", WorkSource.Integration);
        await Submit(joined, "mine");
        await Submit(joined, "request", WorkSource.Integration);

        await queue.Withdraw(alone, "request");
        await queue.Withdraw(joined, "request");
        await queue.Withdraw(joined, "never sent");

        Assert.False(await only.Started);
        Assert.Null(queue.Get(alone));
        Assert.Equal("mine", queue.Get(joined)?.Text);
    }

    [Fact]
    public async Task Withdraw_AlsoTakesTheTextOutOfWorkThatIsJustStartingSoItDoesNotComeBack()
    {
        Guid one = Workspace("one");
        Guid other = Workspace("other");
        await Submit(one, "a");
        WorkTicket waiting = await Submit(other, "request", WorkSource.Integration);
        TaskCompletionSource starting = new();
        TaskCompletionSource fail = new();
        prompts.Setup(x => x.Prompt(other, It.IsAny<string>(), true, It.IsAny<CancellationToken>())).Returns(async () =>
        {
            starting.SetResult();
            await fail.Task;
            // The workspace was taken meanwhile, which puts the work back to wait.
            activity.TryEnter(other, WorkspaceState.Managing);
            throw new RequestException("busy");
        });
        Finish(one);
        await starting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.Withdraw(other, "request");
        fail.SetResult();

        Assert.False(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(queue.Get(other));
    }

    [Fact]
    public async Task AnIntegrationStartsBeforeTheWaitingRunOfALoop()
    {
        Guid one = Workspace("one");
        Guid looping = Workspace("looping");
        await Submit(one, "a");
        await Submit(looping, "loop", WorkSource.Loop);
        await Submit(looping, "request", WorkSource.Integration);

        Finish(one);
        await Until(() => Sent().Count == 2);
        Finish(looping);
        await Until(() => Sent().Count == 3);

        Assert.Equal(["a", "request", "loop"], Sent());
    }

    [Fact]
    public async Task Reserve_WaitsForTheLocalAiHoldsItAndGivesItBackWhenDisposed()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        Guid three = Workspace("three");
        await Submit(one, "a");

        Task<IAsyncDisposable> turn = queue.Reserve(two, CancellationToken.None);
        await Task.Delay(50);

        Assert.False(turn.IsCompleted);
        Assert.Equal((1, string.Empty), (queue.Get(two)!.Position, queue.Get(two)!.Text));
        Finish(one);
        IAsyncDisposable held = await turn.WaitAsync(TimeSpan.FromSeconds(5));
        WorkTicket waiting = await Submit(three, "c");
        Assert.True(waiting.Queued);

        await held.DisposeAsync();
        await held.DisposeAsync();

        Assert.True(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Reserve_IsGivenUpWhenItsQueuedWorkIsCancelled()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        await Submit(one, "a");
        Task<IAsyncDisposable> turn = queue.Reserve(two, CancellationToken.None);
        await Task.Delay(50);

        await queue.Cancel(two);

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => turn);
        Assert.Contains("cancelled", refused.Message);
    }

    [Fact]
    public async Task AQueuedPromptThatCannotStartReportsWhyAndKeepsItsTextAsASavedInput()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        await Submit(one, "a");
        WorkTicket waiting = await Submit(two, "keep me");
        refusal = "OpenCode is missing.";

        Finish(one);

        RequestException failed = await Assert.ThrowsAsync<RequestException>(() => waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("OpenCode is missing.", failed.Message);
        savedInputs.Verify(x => x.Keep(two, "keep me", It.IsAny<CancellationToken>()), Times.Once);
        lock (published)
        {
            Assert.Contains(published, remoteEvent => remoteEvent.Kind == RemoteEventKind.Failed && remoteEvent.WorkspaceId == two && remoteEvent.Error!.Contains("OpenCode is missing."));
        }

        Assert.Equal(1, queue.FreeDelegatedSlots);
    }

    [Fact]
    public async Task AQueuedPromptStoppedWhileItStartsIsNotQueuedAgainAndItsTextIsKeptWithoutAFailure()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        await Submit(one, "a");
        WorkTicket waiting = await Submit(two, "stopped");
        (refusal, halted) = ("Work is being cancelled.", true);

        Finish(one);

        Assert.False(await waiting.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(queue.Waiting);
        savedInputs.Verify(x => x.Keep(two, "stopped", It.IsAny<CancellationToken>()), Times.Once);
        lock (published)
        {
            Assert.DoesNotContain(published, remoteEvent => remoteEvent.Kind == RemoteEventKind.Failed);
        }
    }

    [Fact]
    public async Task OnlyAPromptThatWillNotRunIsMarkedAsDroppedSoClientsCanTellItFromOneThatStarts()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        Guid three = Workspace("three");
        await Submit(one, "a");
        await Submit(two, "b");
        await Submit(three, "c");

        await queue.Cancel(three);
        Finish(one);
        await Until(() => Sent().Count == 2);

        lock (published)
        {
            List<RemoteEvent> emptied = [.. published.Where(remoteEvent => remoteEvent is { Kind: RemoteEventKind.QueueChanged, Queue: null })];
            Assert.Equal("dropped", Assert.Single(emptied, remoteEvent => remoteEvent.WorkspaceId == three).Text);
            Assert.Null(Assert.Single(emptied, remoteEvent => remoteEvent.WorkspaceId == two).Text);
        }
    }

    [Fact]
    public async Task AReportNeverJoinsOtherWorkAndCountsAsStartedOnceAPromptOfTheUserTakesItAlong()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        Guid three = Workspace("three");
        await Submit(one, "a");
        await Submit(two, "mine");
        WorkTicket report = await Submit(three, "report", WorkSource.Report);

        await Assert.ThrowsAsync<RequestException>(() => Submit(two, "another report", WorkSource.Report));
        Assert.False(report.Started.IsCompleted);
        await Submit(three, "and this", WorkSource.Direct);

        Assert.True(await report.Started.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("report\n\nand this", queue.Get(three)?.Text);
        Assert.Equal(2, queue.Get(three)?.Position);
    }

    [Fact]
    public async Task AWaitingReportIsNotRestoredAfterARestartSinceTheSwarmGivesItAgain()
    {
        Guid running = Workspace("running");
        Guid reported = Workspace("reported");
        await queue.Open(temp.Path, CancellationToken.None);
        await Submit(running, "a");
        await Submit(reported, "report", WorkSource.Report);
        busy.Clear();
        WorkQueue restarted = new(workspaces.Object, prompts.Object, host.Object, new WorkspaceActivity(events), savedInputs.Object, new EventHub());

        await restarted.Open(temp.Path, CancellationToken.None);

        Assert.Empty(restarted.Waiting);
    }

    [Fact]
    public async Task NothingThatWaitsStartsOnceTheServerIsStopping()
    {
        Guid one = Workspace("one");
        Guid two = Workspace("two");
        using CancellationTokenSource server = new();
        await queue.Open(temp.Path, server.Token);
        await Submit(one, "a");
        await Submit(two, "b");

        await server.CancelAsync();
        Finish(one);
        await Task.Delay(100);

        Assert.Equal(["a"], Sent());
        Assert.Equal([two], queue.Waiting);
    }

    [Fact]
    public async Task AFailureToStartAtOnceReachesTheCallerAndLeavesTheLocalAiFree()
    {
        Guid one = Workspace("one");
        refusal = "No model.";

        await Assert.ThrowsAsync<RequestException>(() => Submit(one, "a"));
        await Assert.ThrowsAsync<RequestException>(() => Submit(one, "  "));

        Assert.Equal(1, queue.FreeDelegatedSlots);
    }

    [Fact]
    public async Task TheQueueSurvivesARestartButNotLoopRunsNorDelegatedTasksWhichTheSwarmSubmitsAgain()
    {
        Guid running = Workspace("running");
        Guid second = Workspace("second");
        Guid third = Workspace("third");
        Guid looping = Workspace("looping");
        await queue.Open(temp.Path, CancellationToken.None);
        await Submit(running, "a");
        await Submit(second, "b");
        await Submit(third, "c", WorkSource.Delegated);
        await Submit(looping, "loop", WorkSource.Loop);
        await Until(() => File.Exists(Path.Combine(temp.Path, "queue.json")) && File.ReadAllText(Path.Combine(temp.Path, "queue.json")).Contains("loop", StringComparison.OrdinalIgnoreCase));
        busy.Clear();
        sent.Clear();
        WorkspaceActivity fresh = new(events);
        environment = new AiEnvironment();
        WorkQueue restarted = new(workspaces.Object, prompts.Object, host.Object, fresh, savedInputs.Object, new EventHub());

        await restarted.Open(temp.Path, CancellationToken.None);
        restarted.Pump();

        // Held back until whoever takes something out of what was restored has done so.
        await Task.Delay(100);
        Assert.Empty(Sent());
        Assert.Equal("b", restarted.Get(second)?.Text);
        restarted.Resume();

        await Until(() => Sent().Count == 1);
        Assert.Equal(["b"], Sent());
        Assert.Empty(restarted.Waiting);
        Assert.Null(restarted.Get(third));
        Assert.Null(restarted.Get(looping));
    }
}
