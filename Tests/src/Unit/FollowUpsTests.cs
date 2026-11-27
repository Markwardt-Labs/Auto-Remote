namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FollowUpsTests : IDisposable
{
    public FollowUpsTests()
    {
        events.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            lock (published)
            {
                published.Add(remoteEvent);
            }
        });
        events.Setup(x => x.Subscribe(It.IsAny<Action<RemoteEvent>>())).Returns((Action<RemoteEvent> next) =>
        {
            follow = next;
            return Mock.Of<IDisposable>();
        });
        activity = new WorkspaceActivity(events.Object);
        workspaces.Setup(x => x.All(It.IsAny<CancellationToken>())).ReturnsAsync([workspace]);
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => workspace);
        savedInputs.SetupGet(x => x.Waiting).Returns(() => Queued() ? [workspace.Id] : []);
        savedInputs.Setup(x => x.HasQueued(workspace.Id)).Returns(Queued);
        savedInputs.Setup(x => x.SendNext(workspace.Id, It.IsAny<Func<SavedInput, Task>>(), It.IsAny<CancellationToken>())).Returns(async (Guid _, Func<SavedInput, Task> send, CancellationToken _) =>
        {
            string text;

            lock (waiting)
            {
                text = waiting[0];
            }

            await send(new SavedInput { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = true, AutoCommit = commits });

            lock (waiting)
            {
                waiting.Remove(text);
            }

            return true;
        });
        savedInputs.Setup(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>())).Callback(() =>
        {
            lock (waiting)
            {
                waiting.Clear();
            }
        }).Returns(Task.CompletedTask);
        git.Setup(x => x.Status(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        versioning.Setup(x => x.PerformClaimed(workspace, It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>())).Returns(async (WorkspaceInfo _, Func<CancellationToken, Task> action, CancellationToken cancellation) =>
        {
            try
            {
                await action(cancellation);
            }
            finally
            {
                activity.Leave(workspace.Id, WorkspaceState.Managing);
            }
        });
        queue.SetupGet(x => x.RestoredCommits).Returns(() => restored);
        prompts.Setup(x => x.PromptWhenIdle(workspace.Id, It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>())).Returns((Guid _, string _, Action taken, CancellationToken _) =>
        {
            taken();
            return Task.CompletedTask;
        });
        versioning.Setup(x => x.CommitMessage(workspace, It.IsAny<CancellationToken>())).ReturnsAsync("Add the thing");
        guard.Setup(x => x.IsSuspended(workspace.Id)).Returns(() => suspended);
        loops.Setup(x => x.IsRunning(workspace.Id)).Returns(() => loopRun);
        guard.Setup(x => x.Hold(workspace.Id, workspace.Service, It.IsAny<CancellationToken>())).ReturnsAsync(() => overLimit);
        followUps = new FollowUps(workspaces.Object, savedInputs.Object, prompts.Object, queue.Object, activity, versioning.Object, git.Object, loops.Object, guard.Object, events.Object, _ => Task.FromResult(awaited), _ => held, TimeSpan.FromMilliseconds(10), id =>
        {
            reopened.Add(id);
            return Task.CompletedTask;
        });
    }

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<ISavedInputs> savedInputs = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IWorkQueue> queue = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IGitService> git = new();

    private readonly Mock<IWorkspaceLoops> loops = new();

    private readonly Mock<IWindowGuard> guard = new();

    private readonly Mock<IEventHub> events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly WorkspaceActivity activity;

    private readonly CancellationTokenSource stopping = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly List<string> waiting = [];

    private readonly ConcurrentBag<Guid> reopened = [];

    private readonly List<Guid> restored = [];

    private readonly FollowUps followUps;

    private Action<RemoteEvent>? follow;

    private bool awaited;

    private bool held;

    private bool suspended;

    private bool overLimit;

    private bool commits = true;

    private bool loopRun;

    private GitStatus status = new() { IsRepo = true, HasChanges = true, HasIdentity = true };

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    private bool Queued()
    {
        lock (waiting)
        {
            return waiting.Count > 0;
        }
    }

    private void Queue(string text)
    {
        lock (waiting)
        {
            waiting.Add(text);
        }
    }

    private AutoCommitState[] States()
    {
        lock (published)
        {
            return [.. published.Where(item => item.Kind == RemoteEventKind.AutoCommitChanged).Select(item => item.AutoCommit!.Value)];
        }
    }

    private string[] Failures()
    {
        lock (published)
        {
            return [.. published.Where(item => item.Kind == RemoteEventKind.Failed).Select(item => item.Error!)];
        }
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    // The AI works with auto commit on.
    private void Armed()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        followUps.SetAutoCommit(workspace.Id, true);
    }

    private void Committed(Times times) => git.Verify(x => x.Commit(workspace.Folder, "Add the thing", It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task Complete_TakesTheWorkspaceStraightOnToCommitWithTheMessageTheAiWrites()
    {
        Armed();

        bool taken = followUps.Complete(workspace.Id, true);

        Assert.True(taken);
        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off);
        Committed(Times.Once());
        Assert.Equal([AutoCommitState.On, AutoCommitState.Committing, AutoCommitState.Off], States());
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.Empty(Failures());
    }

    [Theory]
    [InlineData(false, WorkspaceState.Working)]
    [InlineData(true, WorkspaceState.Stopping)]
    [InlineData(true, WorkspaceState.Canceling)]
    public void Complete_CommitsNothingOfWorkThatFailedOrIsBeingStoppedHoweverTheAiEnded(bool succeeded, WorkspaceState state)
    {
        Armed();
        Queue("next");

        if (state != WorkspaceState.Working)
        {
            activity.Halt(workspace.Id, state);
        }

        bool taken = followUps.Complete(workspace.Id, succeeded);

        Assert.False(taken);
        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        Committed(Times.Never());
    }

    [Fact]
    public void Complete_LeavesWorkOfTheAiInsideAGitActionAlone()
    {
        Armed();
        activity.Pass(workspace.Id, WorkspaceState.Working, WorkspaceState.Managing);

        Assert.False(followUps.Complete(workspace.Id, true));

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task Commit_WaitsWhileTheWorkIsPausedAtTheUsageLimit()
    {
        await followUps.Open(stopping.Token);
        Armed();
        suspended = true;

        Assert.False(followUps.Complete(workspace.Id, true));
        activity.Leave(workspace.Id, WorkspaceState.Working);
        await Task.Delay(80);

        Assert.True(followUps.IsPending(workspace.Id));
        Committed(Times.Never());

        suspended = false;

        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off);
        Committed(Times.Once());
    }

    [Fact]
    public async Task Commit_WaitsWhileTheAiIsStillToHearOfTheTasksItDelegated()
    {
        await followUps.Open(stopping.Token);
        Armed();
        awaited = true;

        Assert.True(followUps.Complete(workspace.Id, true));
        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.On && activity.Get(workspace.Id) == WorkspaceState.Ready);
        await Task.Delay(80);

        Assert.True(followUps.IsPending(workspace.Id));
        Committed(Times.Never());
        Assert.Equal([AutoCommitState.On, AutoCommitState.Committing, AutoCommitState.On], States());

        awaited = false;

        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off);
        Committed(Times.Once());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Commit_DoesNothingWithoutARepositoryOrAnythingToCommit(bool isRepo, bool hasChanges)
    {
        status = new GitStatus { IsRepo = isRepo, HasChanges = hasChanges, HasIdentity = true };
        Armed();

        followUps.Complete(workspace.Id, true);

        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off && activity.Get(workspace.Id) == WorkspaceState.Ready);
        Committed(Times.Never());
        Assert.Empty(Failures());
    }

    [Fact]
    public async Task Commit_ThatCannotBeMadeIsReportedAndPausesTheQueue()
    {
        status = new GitStatus { IsRepo = true, HasChanges = true, HasIdentity = false };
        Armed();
        Queue("next");

        followUps.Complete(workspace.Id, true);

        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off);
        Assert.StartsWith("Auto commit failed: Git needs your name and email", Assert.Single(Failures()));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public void SetAutoCommit_NeedsWorkUnderWayThatIsNotARunOfALoop()
    {
        Assert.Throws<RequestException>(() => followUps.SetAutoCommit(workspace.Id, true));
        followUps.SetAutoCommit(workspace.Id, false);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        // A loop that is between its runs does not stand in the way.
        loops.Setup(x => x.Get(workspace.Id)).Returns(new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch });
        loopRun = true;

        Assert.Throws<RequestException>(() => followUps.SetAutoCommit(workspace.Id, true));
        Assert.False(followUps.Arm(workspace.Id));
        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));

        loopRun = false;
        followUps.SetAutoCommit(workspace.Id, true);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task Arm_SaysWhetherItTurnedAutoCommitOnSoItCanBeTurnedOffAgain()
    {
        Assert.True(followUps.Arm(workspace.Id));
        Assert.False(followUps.Arm(workspace.Id));

        followUps.Disarm(workspace.Id);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Complete_CommitsWorkGivenBetweenTheRunsOfALoopAndARunThatGoesWellResumesNothing()
    {
        loops.Setup(x => x.Get(workspace.Id)).Returns(new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch });
        Armed();

        Assert.True(followUps.Complete(workspace.Id, true));
        await Until(() => followUps.Get(workspace.Id) == AutoCommitState.Off && activity.Get(workspace.Id) == WorkspaceState.Ready);
        Committed(Times.Once());

        savedInputs.Invocations.Clear();
        Queue("paused");
        loopRun = true;
        activity.TryEnter(workspace.Id, WorkspaceState.Working);

        Assert.False(followUps.Complete(workspace.Id, true));

        savedInputs.Verify(x => x.SetPaused(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GoesFirst_SaysSoForACommitThatIsDueAndForAQueuedInputThatIsToBeSent()
    {
        await followUps.Open(stopping.Token);
        Assert.False(followUps.GoesFirst(workspace.Id));
        held = true;

        Queue("next");

        Assert.True(followUps.GoesFirst(workspace.Id));

        lock (waiting)
        {
            waiting.Clear();
        }

        Assert.False(followUps.GoesFirst(workspace.Id));
        Armed();
        suspended = true;
        followUps.Complete(workspace.Id, true);

        Assert.True(followUps.GoesFirst(workspace.Id));
    }

    [Fact]
    public async Task QueuedInput_RunsBetweenTheRunsOfALoopButNotWhileOneIsUnderWay()
    {
        loops.Setup(x => x.Get(workspace.Id)).Returns(new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch });
        loopRun = true;
        await followUps.Open(stopping.Token);
        Queue("next");
        await Task.Delay(80);
        prompts.Verify(x => x.PromptWhenIdle(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Never);

        loopRun = false;

        await Until(() => !Queued());
        prompts.Verify(x => x.PromptWhenIdle(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuedInput_KeptWithAutoCommitOffIsSentWithoutIt()
    {
        commits = false;
        await followUps.Open(stopping.Token);

        Queue("next");

        await Until(() => !Queued());
        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        Assert.Empty(States());
    }

    [Fact]
    public async Task AutoCommit_GoesOffWhenTheWaitingWorkIsDroppedAndALoopThatStartsLeavesItOn()
    {
        await followUps.Open(stopping.Token);
        Armed();

        follow!(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch } });

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));

        follow!(new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspace.Id, Text = "dropped" });

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
    }

    [Theory]
    [InlineData(WorkspaceState.Stopping)]
    [InlineData(WorkspaceState.Canceling)]
    public async Task AutoCommit_GoesOffAndTheQueuePausesAsSoonAsTheWorkIsAskedToStop(WorkspaceState state)
    {
        await followUps.Open(stopping.Token);
        savedInputs.Invocations.Clear();
        held = true;
        Armed();
        Queue("next");

        follow!(new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = state });

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Open_PausesWhatWasLeftQueued()
    {
        await followUps.Open(stopping.Token);

        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuedInput_IsSentWithAutoCommitOnOnceNothingIsInItsWay()
    {
        await followUps.Open(stopping.Token);
        held = true;
        overLimit = true;
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        queue.Setup(x => x.Get(workspace.Id)).Returns(new QueueStatus { Position = 1 });
        Queue("next");
        await Task.Delay(60);
        activity.Leave(workspace.Id, WorkspaceState.Managing);
        await Task.Delay(60);
        queue.Setup(x => x.Get(workspace.Id)).Returns((QueueStatus?)null);
        await Task.Delay(60);
        held = false;
        await Task.Delay(60);

        prompts.Verify(x => x.PromptWhenIdle(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Never);

        overLimit = false;

        await Until(() => !Queued());
        prompts.Verify(x => x.PromptWhenIdle(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task QueuedInput_OfASideSessionGivesItNewWorkSoAnEarlierMarkOfCompletionNoLongerCounts()
    {
        WorkspaceInfo side = workspace with { Side = new SideSession { ParentId = Guid.NewGuid(), ParentName = "app", Name = "side", State = SideSessionState.Ready, StartedFrom = SideSessionStart.Commit } };
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(side);
        await followUps.Open(stopping.Token);

        Queue("next");

        await Until(() => !Queued());
        Assert.Equal([workspace.Id], reopened);
    }

    [Fact]
    public async Task QueuedInput_OfALocalWorkspaceTakesItsTurnForTheLocalAi()
    {
        WorkspaceInfo local = workspace with { Service = AiService.Local };
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(local);
        queue.Setup(x => x.SubmitWhenFree(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkTicket(true, Task.FromResult(true)));
        await followUps.Open(stopping.Token);

        Queue("next");

        await Until(() => !Queued());
        queue.Verify(x => x.SubmitWhenFree(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Once);
        prompts.Verify(x => x.PromptWhenIdle(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueuedInput_ThatCannotBeSentIsReportedAndPaused()
    {
        prompts.Setup(x => x.PromptWhenIdle(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>())).Returns((Guid _, string _, Action taken, CancellationToken _) =>
        {
            taken();
            return Task.FromException(new RequestException("The tool is missing."));
        });
        await followUps.Open(stopping.Token);

        Queue("next");

        await Until(() => Failures().Length > 0);
        Assert.Equal("A queued input could not be sent: The tool is missing.", Failures()[0]);
        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    [Fact]
    public async Task ResumeQueue_ResumesThePausedInputs()
    {
        await followUps.ResumeQueue(workspace.Id, CancellationToken.None);

        savedInputs.Verify(x => x.SetPaused(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Abandon_TurnsAutoCommitOffAndPausesTheQueue()
    {
        Armed();
        Queue("next");

        await followUps.Abandon(workspace.Id);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void IsHolding_CountsQueuedInputsPausedOrNot()
    {
        Assert.False(followUps.IsHolding(workspace.Id));

        Queue("next");

        Assert.True(followUps.IsHolding(workspace.Id));
    }

    [Fact]
    public async Task SetAutoCommit_AndAStopThatComesAtTheSameTimeDoNotLockEachOtherUp()
    {
        await followUps.Open(stopping.Token);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        using ManualResetEventSlim halting = new();
        events.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            if (remoteEvent.State == WorkspaceState.Stopping)
            {
                // The stop is told while the states are locked, and the toggle comes just then.
                halting.Set();
                Thread.Sleep(100);
                follow!(remoteEvent);
            }
        });

        Task stop = Task.Run(() => activity.Halt(workspace.Id, WorkspaceState.Stopping));
        Task toggle = Task.Run(() =>
        {
            halting.Wait();
            Assert.Throws<RequestException>(() => followUps.SetAutoCommit(workspace.Id, true));
        });

        await Task.WhenAll(stop, toggle).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task QueuedInput_ThatLosesTheWorkspaceToOtherWorkLeavesItsAutoCommitAlone()
    {
        commits = false;
        int attempts = 0;
        prompts.Setup(x => x.PromptWhenIdle(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>())).Returns(() =>
        {
            // The user's prompt, sent with auto commit, got there first.
            Interlocked.Increment(ref attempts);
            followUps.Arm(workspace.Id);
            activity.TryEnter(workspace.Id, WorkspaceState.Working);
            return Task.FromException(new RequestException("The AI is working."));
        });
        await followUps.Open(stopping.Token);

        Queue("next");
        await Until(() => Volatile.Read(ref attempts) > 0);
        await Task.Delay(80);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
        Assert.True(Queued());
        Assert.Empty(Failures());
    }

    [Fact]
    public async Task QueuedInput_WaitsForWorkThatIsOnItsWayToTheLocalAi()
    {
        queue.Setup(x => x.IsStarting(workspace.Id)).Returns(true);
        await followUps.Open(stopping.Token);

        Queue("next");
        await Task.Delay(80);

        prompts.Verify(x => x.PromptWhenIdle(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Complete_OfWorkThatWasGivenUpResumesNothingThoughItEndsWell()
    {
        savedInputs.Setup(x => x.HasQueued(workspace.Id)).Returns(true);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);
        await followUps.Abandon(workspace.Id);

        followUps.Complete(workspace.Id, true);

        savedInputs.Verify(x => x.SetPaused(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Never);

        // The work after it counts again.
        followUps.Complete(workspace.Id, true);

        savedInputs.Verify(x => x.SetPaused(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Commit_ThatIsStoppedBeforeItHasAnythingToCancelResumesNothingAndComesNoMore(bool whileAwaited)
    {
        status = new GitStatus { IsRepo = true, HasChanges = false, HasIdentity = true };
        TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<GitStatus> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int asked = 0;
        FollowUps stopped = new(workspaces.Object, savedInputs.Object, prompts.Object, queue.Object, activity, versioning.Object, git.Object, loops.Object, guard.Object, events.Object, _ =>
        {
            Interlocked.Increment(ref asked);
            return whileAwaited ? answer.Task : Task.FromResult(false);
        }, null, TimeSpan.FromMilliseconds(10));
        git.Setup(x => x.Status(workspace.Folder, It.IsAny<CancellationToken>())).Returns(() =>
        {
            Interlocked.Increment(ref asked);
            return read.Task;
        });
        await stopped.Open(stopping.Token);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        stopped.SetAutoCommit(workspace.Id, true);
        Assert.True(stopped.Complete(workspace.Id, true));
        await Until(() => Volatile.Read(ref asked) >= (whileAwaited ? 1 : 2));
        savedInputs.Invocations.Clear();

        activity.Halt(workspace.Id, WorkspaceState.Stopping);
        follow!(new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = WorkspaceState.Stopping });
        activity.Leave(workspace.Id, WorkspaceState.Stopping);
        // A git action the user starts right after is not the commit's to end.
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        answer.SetResult(true);
        read.SetResult(status);

        await Until(() => stopped.Get(workspace.Id) == AutoCommitState.Off);
        await Task.Delay(80);
        Assert.Equal(AutoCommitState.Off, stopped.Get(workspace.Id));
        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));
        savedInputs.Verify(x => x.SetPaused(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Abort_TurnsOffWhatWasTurnedOnForWorkThatNeverStartedButNotACommitStillDue()
    {
        Armed();

        followUps.Abort(workspace.Id);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));

        followUps.SetAutoCommit(workspace.Id, true);
        suspended = true;
        followUps.Complete(workspace.Id, true);
        followUps.Abort(workspace.Id);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task Open_TurnsAutoCommitOnAgainForRestoredWorkThatIsToBeCommitted()
    {
        restored.Add(workspace.Id);

        await followUps.Open(stopping.Token);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task Pump_ForgetsTheQueueOfAWorkspaceThatIsGone()
    {
        await followUps.Open(stopping.Token);
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Unknown workspace."));
        savedInputs.Setup(x => x.Forget(workspace.Id)).Callback(() =>
        {
            lock (waiting)
            {
                waiting.Clear();
            }
        });

        Queue("next");

        await Until(() => !Queued());
        savedInputs.Verify(x => x.Forget(workspace.Id), Times.Once);
        Assert.Empty(Failures());
    }
}
