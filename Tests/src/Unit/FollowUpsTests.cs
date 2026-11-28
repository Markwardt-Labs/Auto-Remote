namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FollowUpsTests : IAsyncLifetime
{
    public FollowUpsTests()
    {
        activity = new WorkspaceActivity(events);
        events.Subscribe(remoteEvent =>
        {
            lock (published)
            {
                published.Add(remoteEvent);
            }

            if (remoteEvent.Kind == RemoteEventKind.AutoCommitChanged)
            {
                Log($"commit:{remoteEvent.AutoCommit}");
            }
        });
        workspaces.Setup(x => x.All(It.IsAny<CancellationToken>())).ReturnsAsync(() => [workspace]);
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => workspace);
        store.Setup(x => x.LoadInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. inputs]);
        store.Setup(x => x.SaveInputs(workspace.Id, It.IsAny<IReadOnlyList<SavedInput>>(), It.IsAny<CancellationToken>())).Returns((Guid _, IReadOnlyList<SavedInput> saved, CancellationToken _) =>
        {
            inputs = [.. saved];
            return Task.CompletedTask;
        });
        savedInputs = new SavedInputs(store.Object, workspaces.Object, events, Mock.Of<ISpeechService>());
        queue.SetupGet(x => x.RestoredCommits).Returns(() => restored);
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(() => busy);
        prompts.Setup(x => x.PromptWhenIdle(workspace.Id, It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>())).Returns((Guid _, string text, Action taken, CancellationToken _) =>
        {
            if (refusal is { } reason)
            {
                return Task.FromException(new RequestException(reason));
            }

            Take(text, taken);
            return Task.CompletedTask;
        });
        git.Setup(x => x.Status(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        git.Setup(x => x.Commit(workspace.Folder, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback(() => Log("committed")).Returns(Task.CompletedTask);
        git.Setup(x => x.IsPushed(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync(() => pushed);
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
        versioning.Setup(x => x.CommitMessage(workspace, It.IsAny<CancellationToken>())).ReturnsAsync("Add the thing");
        guard.Setup(x => x.IsSuspended(workspace.Id)).Returns(() => suspended);
        loops.Setup(x => x.IsRunning(workspace.Id)).Returns(() => loopRun);
        journal = new ActivityLog(events);
        followUps = Create();
    }

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IWorkQueue> queue = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IGitService> git = new();

    private readonly Mock<IWorkspaceLoops> loops = new();

    private readonly Mock<IWindowGuard> guard = new();

    private readonly EventHub events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly List<string> log = [];

    private readonly List<string> briefs = [];

    private readonly SavedInputs savedInputs;

    private readonly ActivityLog journal;

    private readonly WorkspaceActivity activity;

    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly List<Guid> restored = [];

    private FollowUps followUps;

    private AutomationStore automation = new();

    private List<SavedInput> inputs = [];

    private volatile bool busy;

    private bool awaited;

    private bool held;

    private bool suspended;

    private bool loopRun;

    private bool? pushed = true;

    private volatile string? refusal;

    private GitStatus status = new() { IsRepo = true, HasChanges = true, HasIdentity = true };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await stopping.CancelAsync();
        stopping.Dispose();
        await Task.Delay(30);
        await automation.Flush();
        await journal.Flush();
        temp.Dispose();
    }

    private FollowUps Create()
    {
        automation = new AutomationStore();
        return new FollowUps(workspaces.Object, savedInputs, prompts.Object, queue.Object, activity, versioning.Object, git.Object, loops.Object, guard.Object, events, journal, automation, new FailureKinds([TimeSpan.FromMilliseconds(20)]), _ => Task.FromResult(awaited), _ => held, TimeSpan.FromMilliseconds(10));
    }

    private Task Open() => followUps.Open(temp.Path, stopping.Token);

    private void Log(string line)
    {
        lock (log)
        {
            log.Add(line);
        }
    }

    private List<string> Lines()
    {
        lock (log)
        {
            return [.. log];
        }
    }

    private List<string> Sent() => [.. Lines().Where(line => line.StartsWith("sent:", StringComparison.Ordinal)).Select(line => line[5..])];

    private int Commits() => Lines().Count(line => line == "committed");

    // What the AI service does when it takes a prompt.
    private void Take(string text, Action? taken)
    {
        Assert.True(activity.TryEnter(workspace.Id, WorkspaceState.Working));
        taken?.Invoke();
        string? brief = followUps.Brief(workspace.Id, text);
        busy = true;
        Log($"sent:{text}");

        lock (briefs)
        {
            briefs.Add(brief ?? string.Empty);
        }

        followUps.Began(workspace.Id, text);
    }

    // A message the user sends while the AI is free.
    private void Send(string text) => Take(text, null);

    private void End(bool answered = true, bool failed = false, bool stopped = false, string? error = null)
    {
        busy = false;

        if (!followUps.Complete(workspace.Id, new WorkResult(answered, failed || !stopped && !answered, stopped, error, answered ? "Done, assuming the default." : null)))
        {
            activity.Leave(workspace.Id, WorkspaceState.Working);
        }
    }

    private async Task Stop()
    {
        activity.Halt(workspace.Id, WorkspaceState.Stopping);
        End(stopped: true);
        activity.Leave(workspace.Id, WorkspaceState.Stopping);
        await Task.Delay(50);
    }

    private Task<IReadOnlyList<SavedInput>> Queue(string text, bool commit = true) => followUps.Queue(workspace.Id, text, commit, CancellationToken.None);

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), string.Join(", ", Lines()));
    }

    private async Task<IReadOnlyList<ActivityEntry>> Entries()
    {
        // A commit is seen being made before its outcome is recorded.
        await Until(() => !followUps.IsCommitting(workspace.Id));
        await journal.Flush();
        return (await journal.Read(workspace.Id, null, 50, CancellationToken.None)).Entries;
    }

    [Fact]
    public async Task Send_NeverTurnsAutoCommitOnSoAQuestionIsNotCommitted()
    {
        await Open();

        Send("should we use sqlite?");
        End();
        await Task.Delay(50);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        Assert.Equal(0, Commits());
        Assert.Equal([string.Empty], briefs);
        ActivityEntry entry = Assert.Single(await Entries());
        Assert.Equal((ActivitySource.Send, ActivityOutcome.Done, "Done, assuming the default."), (entry.Source, entry.Outcome, entry.Report));
    }

    [Fact]
    public async Task SendToCommit_CommitsTheWorkOfTheMessageEvenWhenItEndsBeforeSendingDoes()
    {
        await Open();

        int answer = await followUps.SendToCommit(workspace.Id, () =>
        {
            Send("do it");
            End();
            return Task.FromResult(7);
        });

        await Until(() => Commits() == 1);
        Assert.Equal(7, answer);
        Assert.NotNull(Assert.Single(await Entries()).Commit);
    }

    [Fact]
    public async Task SendToCommit_CommitsOnlyThatWorkSoTheNextSentMessageIsNot()
    {
        await Open();
        await followUps.SendToCommit(workspace.Id, () => Sending("do it"));
        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
        End();
        await Until(() => Commits() == 1 && !followUps.IsCommitting(workspace.Id));
        await Until(() => activity.Get(workspace.Id) == WorkspaceState.Ready);

        Send("and a question?");
        End();
        await Task.Delay(80);

        Assert.Equal((1, AutoCommitState.Off), (Commits(), followUps.Get(workspace.Id)));
    }

    [Fact]
    public async Task SendToCommit_OfAMessageThatJoinsWorkHasThatWorkCommittedAlsoOverTheUsersEarlierChoice()
    {
        await Open();
        Send("long job");
        followUps.SetAutoCommit(workspace.Id, false);

        await followUps.SendToCommit(workspace.Id, () => Task.FromResult(true));
        followUps.Reconsider(workspace.Id);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
        End();
        await Until(() => Commits() == 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendToCommit_WhoseSendFails_LeavesTheCommitAsItWas(bool committing)
    {
        await Open();
        Send("long job");

        if (committing)
        {
            followUps.SetAutoCommit(workspace.Id, true);
        }

        await Assert.ThrowsAsync<RequestException>(() => followUps.SendToCommit<bool>(workspace.Id, () => Task.FromException<bool>(new RequestException("refused"))));

        Assert.Equal(committing ? AutoCommitState.On : AutoCommitState.Off, followUps.Get(workspace.Id));
        End();
        await Until(() => activity.Get(workspace.Id) == WorkspaceState.Ready);
        await Task.Delay(80);
        Assert.Equal(committing ? 1 : 0, Commits());
    }

    [Fact]
    public async Task SendToCommit_DoesNotUndoACommitTheUserTurnedOffWhileTheMessageWasOnItsWay()
    {
        await Open();

        await followUps.SendToCommit(workspace.Id, () =>
        {
            Send("do it");
            followUps.SetAutoCommit(workspace.Id, false);
            return Task.FromResult(true);
        });

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
    }

    private Task<bool> Sending(string text)
    {
        Send(text);
        return Task.FromResult(true);
    }

    [Fact]
    public async Task QueueWithCommit_CommitsTheWorkUnderWayBeforeTheQueuedInputWhichGetsACommitOfItsOwn()
    {
        await Open();
        Send("build login");

        await Queue("add tests");

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
        End();
        await Until(() => Sent().Count == 2);
        Assert.Equal(1, Commits());
        List<string> lines = Lines();
        Assert.True(lines.IndexOf("committed") < lines.IndexOf("sent:add tests"));
        End();
        await Until(() => Commits() == 2);
        await Until(() => activity.Get(workspace.Id) == WorkspaceState.Ready);
        IReadOnlyList<ActivityEntry> entries = await Entries();
        Assert.Equal(["add tests", "build login"], entries.Select(entry => entry.Title).ToList());
        Assert.All(entries, entry => Assert.Equal((ActivityOutcome.Done, "Add the thing", true), (entry.Outcome, entry.Commit!, entry.Pushed!.Value)));
    }

    [Fact]
    public async Task QueueWithoutCommit_LeavesTheWorkUnderWayAsItIs()
    {
        await Open();
        Send("look around");

        await Queue("next", false);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        End();
        await Until(() => Sent().Count == 2);
        End();
        await Task.Delay(50);
        Assert.Equal(0, Commits());
    }

    [Fact]
    public async Task QueueWithCommit_KeepsAnEarlierQueuedStepUncommittedSoTheTwoGoTogether()
    {
        await Open();
        await Queue("first part", false);
        await Until(() => Sent().Count == 1);

        await Queue("second part");

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        End();
        await Until(() => Sent().Count == 2);
        Assert.Equal(0, Commits());
        End();
        await Until(() => Commits() == 1);
    }

    [Fact]
    public async Task QueueWithCommit_NeverOverridesTheUserWhoTurnedTheCommitOff()
    {
        await Open();
        Send("experiment");
        followUps.SetAutoCommit(workspace.Id, true);
        followUps.SetAutoCommit(workspace.Id, false);

        await Queue("next");

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
    }

    [Theory]
    [InlineData(false, AutoCommitState.Off)]
    [InlineData(true, AutoCommitState.On)]
    public async Task RemovingWhatWasQueued_TurnsOffOnlyACommitThatQueuingTurnedOn(bool chosen, AutoCommitState expected)
    {
        await Open();
        Send("build");

        if (chosen)
        {
            followUps.SetAutoCommit(workspace.Id, true);
        }

        SavedInput queued = Assert.Single(await Queue("next"));
        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));

        await savedInputs.Take(workspace.Id, queued.Id, CancellationToken.None);
        followUps.Reconsider(workspace.Id);

        Assert.Equal(expected, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task QueuedWork_IsToldItRunsUnattendedAndTheNextTurnWhatWasNotFinished()
    {
        await Open();
        await Queue("needs a key");
        await Until(() => Sent().Count == 1);
        Assert.Contains("runs unattended", briefs[0]);
        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));

        followUps.Unfinished(workspace.Id, "There is no API key.");
        End();
        await Queue("something else");
        await Until(() => Sent().Count == 2);

        Assert.Equal(0, Commits());
        Assert.Contains("\"needs a key\" was not finished: There is no API key.", briefs[1]);
        ActivityEntry unfinished = (await Entries())[^1];
        Assert.Equal((ActivityOutcome.Unfinished, true, "There is no API key."), (unfinished.Outcome, unfinished.NeedsAttention, unfinished.Reason));

        // What the AI heard is not said again.
        End();
        await Until(() => Commits() == 1);
        Send("how is it going?");
        Assert.Equal(string.Empty, briefs[2]);
    }

    [Fact]
    public async Task WorkThatCannotRun_IsTriedAgainAndTheQueueWaitsForIt()
    {
        await Open();
        await Queue("first");
        await Until(() => Sent().Count == 1);
        await Queue("second");

        for (int attempt = 2; attempt <= 5; attempt++)
        {
            End(answered: false, error: "You have reached your usage limit.");
            await Until(() => Sent().Count == attempt);
        }

        Assert.All(Sent(), text => Assert.Equal("first", text));
        Assert.Equal(["second"], inputs.Select(input => input.Text).ToList());
        End();
        await Until(() => Sent().Count == 6);
        Assert.Equal("second", Sent()[5]);
        Assert.Equal(1, Commits());
    }

    [Fact]
    public async Task WorkThatKeepsFailing_IsGivenUpAfterThreeAttemptsAndTheQueueGoesOn()
    {
        await Open();
        await Queue("broken");
        await Until(() => Sent().Count == 1);
        await Queue("fine");

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            End(failed: true, error: "The AI crashed.");
            await Until(() => Sent().Count == attempt + 1);
        }

        Assert.Equal(["broken", "broken", "broken", "fine"], Sent());
        Assert.Contains("ended with an error: The AI crashed.", briefs[1]);
        Assert.Contains("failed 3 times", briefs[3]);
        Assert.False(followUps.IsStopped(workspace.Id));
        Assert.Contains(await Entries(), entry => entry is { Title: "broken", Outcome: ActivityOutcome.Failed, NeedsAttention: true });
    }

    [Fact]
    public async Task Stop_HoldsTheQueueUntilTheUserResumesWhichASendAndQueuingDoNot()
    {
        await Open();
        await Queue("first");
        await Until(() => Sent().Count == 1);
        await Queue("second");

        await Stop();
        Assert.True(followUps.IsStopped(workspace.Id));
        Send("what happened?");
        End();
        await Queue("third");
        await Task.Delay(100);

        Assert.Equal(["first", "what happened?"], Sent());
        Assert.Equal(0, Commits());

        followUps.Resume(workspace.Id);

        await Until(() => Sent().Count == 3);
        Assert.Equal("second", Sent()[2]);
    }

    [Fact]
    public async Task Stop_OfAMessageWithNothingThatRunsByItselfStopsNothingElse()
    {
        await Open();
        Send("hello");

        await Stop();

        Assert.False(followUps.IsStopped(workspace.Id));
    }

    [Fact]
    public async Task AQueuedInputThatCannotBeSentWhileTheAiCannotRun_StaysQueuedAndIsSentLater()
    {
        await Open();
        refusal = "The connection to the service timed out.";
        await Queue("next");
        await Task.Delay(200);
        Assert.Equal(["next"], inputs.Where(input => input.IsQueued).Select(input => input.Text).ToList());

        refusal = null;

        await Until(() => Sent().Count == 1);
    }

    [Fact]
    public async Task AQueuedInputThatCannotBeSentByItsOwnFault_IsLeftAmongTheDraftsAfterThreeTries()
    {
        await Open();
        refusal = "The attached file is gone.";

        await Queue("next");

        await Until(() => inputs.Count == 1 && !inputs[0].IsQueued);
        Assert.Contains(await Entries(), entry => entry is { Outcome: ActivityOutcome.Failed, NeedsAttention: true });
    }

    [Fact]
    public async Task AfterARestart_WorkThatWasUnderWayIsTakenUpAgainAndSaysSo()
    {
        await Open();
        await Queue("long job");
        await Until(() => Sent().Count == 1);
        await automation.Flush();
        await stopping.CancelAsync();
        busy = false;
        activity.Leave(workspace.Id, WorkspaceState.Working);
        using CancellationTokenSource again = new();
        followUps = Create();

        await followUps.Open(temp.Path, again.Token);

        await Until(() => Sent().Count == 2);
        Assert.Equal("long job", Sent()[1]);
        Assert.Contains("The server restarted while you were working on this", briefs[1]);
        End();
        await Until(() => Commits() == 1);
        await again.CancelAsync();
    }

    [Fact]
    public async Task AfterThreeRestartsDuringTheSameWork_ItIsGivenUp()
    {
        WorkStep step = new() { Source = ActivitySource.Queue, Text = "crashes the server", Commit = true, Started = true, Interruptions = 2 };
        AutomationStore earlier = new();
        await earlier.Open(temp.Path, CancellationToken.None);
        earlier.Save(() => new Dictionary<Guid, StoredAutomation> { [workspace.Id] = new() { Step = step, IsArmed = true } });
        await earlier.Flush();

        await Open();
        await Task.Delay(100);

        Assert.Empty(Sent());
        Assert.Null(followUps.Describe(workspace.Id).Retry);
        Send("next");
        Assert.Contains("was given up", briefs[0]);
    }

    [Fact]
    public async Task AfterARestart_ACommitThatWasStillToComeIsMade()
    {
        AutomationStore earlier = new();
        await earlier.Open(temp.Path, CancellationToken.None);
        earlier.Save(() => new Dictionary<Guid, StoredAutomation> { [workspace.Id] = new() { Step = new WorkStep { Source = ActivitySource.Queue, Text = "done", Started = true }, IsCommitDue = true } });
        await earlier.Flush();

        await Open();

        await Until(() => Commits() == 1);
        Assert.Empty(Sent());
    }

    [Fact]
    public async Task AStoppedChat_StaysStoppedAfterARestart()
    {
        AutomationStore earlier = new();
        await earlier.Open(temp.Path, CancellationToken.None);
        earlier.Save(() => new Dictionary<Guid, StoredAutomation> { [workspace.Id] = new() { IsStopped = true } });
        await earlier.Flush();
        inputs = [new SavedInput { Id = Guid.NewGuid(), Text = "later", Time = DateTimeOffset.UtcNow, IsQueued = true }];

        await Open();
        await Task.Delay(100);

        Assert.Empty(Sent());
        Assert.True(followUps.IsStopped(workspace.Id));
    }

    [Fact]
    public async Task DelegatedTasks_DoNotHoldTheCommitBackButTheQueueWaitsForThem()
    {
        await Open();
        Send("delegate the rest");
        followUps.SetAutoCommit(workspace.Id, true);
        awaited = true;
        await Queue("after the results");

        End();

        await Until(() => Commits() == 1);
        await Task.Delay(100);
        Assert.Single(Sent());
        Assert.True(followUps.MayBringIn(workspace.Id) || activity.Get(workspace.Id) != WorkspaceState.Ready);
        awaited = false;
        await Until(() => Sent().Count == 2);
    }

    [Fact]
    public async Task WorkPausedAtTheUsageLimit_IsCommittedOnceItWasResumedAndEnded()
    {
        await Open();
        Send("big job");
        followUps.SetAutoCommit(workspace.Id, true);
        suspended = true;

        End();
        await Task.Delay(60);
        Assert.Equal(0, Commits());
        Assert.True(followUps.IsPending(workspace.Id));

        // The server sends the resume while the workspace still counts as paused, and releases it after.
        Send("resume unfinished work");
        suspended = false;
        End();

        await Until(() => Commits() == 1);
        Assert.Contains(await Entries(), entry => entry is { Title: "big job", Outcome: ActivityOutcome.Done });
    }

    [Fact]
    public async Task ACommitThatCannotBePushed_IsFlaggedAndToldToTheNextTurnWithoutHoldingAnythingUp()
    {
        await Open();
        pushed = false;
        await Queue("first");
        await Until(() => Sent().Count == 1);
        await Queue("second");

        End();

        await Until(() => Sent().Count == 2);
        Assert.Contains("could not be pushed", briefs[1]);
        Assert.Contains(await Entries(), entry => entry is { Title: "first", NeedsAttention: true, Pushed: false, CommitError: "The commit could not be pushed." });
    }

    [Fact]
    public async Task ACommitThatCannotBeMade_IsReportedWithoutStoppingTheQueue()
    {
        await Open();
        status = new GitStatus { IsRepo = true, HasChanges = true, HasIdentity = false };
        await Queue("first");
        await Until(() => Sent().Count == 1);
        await Queue("second", false);

        End();

        await Until(() => Sent().Count == 2);
        Assert.False(followUps.IsStopped(workspace.Id));
        // The server still tells of the work that went on, so the list is only read under its lock.
        lock (published)
        {
            Assert.Contains(published, item => item.Kind == RemoteEventKind.Failed && item.Error!.StartsWith("Auto commit failed: Git needs your name", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task WorkThatLeavesSomethingUndone_AsItsAskerSays_CountsAsUnfinished()
    {
        await Open();
        List<WorkEnd> ends = [];
        WorkStep step = new() { Source = ActivitySource.BringIn, Text = "bring it in", Commit = true };
        followUps.Expect(workspace.Id, step, ends.Add, () => "It was not confirmed.");
        Take("bring it in", () => followUps.Arm(workspace.Id));
        followUps.Sent(workspace.Id, step.Id);

        End();

        await Until(() => ends.Count == 1);
        Assert.Equal((ActivityOutcome.Unfinished, "It was not confirmed."), (ends[0].Outcome, ends[0].Error));
        Assert.Equal(0, Commits());
    }

    [Fact]
    public async Task AQueuedInputOfALocalWorkspace_TakesItsTurnForTheLocalAi()
    {
        WorkspaceInfo local = workspace with { Service = AiService.Local };
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(local);
        queue.Setup(x => x.SubmitWhenFree(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkTicket(true, Task.FromResult(true)));
        await Open();

        await Queue("next");

        await Until(() => inputs.Count == 0);
        queue.Verify(x => x.SubmitWhenFree(workspace.Id, "next", It.IsAny<Action>(), It.IsAny<CancellationToken>()), Times.Once);
        await Until(() => followUps.Describe(workspace.Id).Step is { Started: false });
    }

    [Fact]
    public async Task QueuedInputs_WaitWhileOtherWorkIsToBeBroughtInOrOnItsWayToTheAi()
    {
        await Open();
        held = true;
        await Queue("next");
        await Task.Delay(80);
        queue.Setup(x => x.IsStarting(workspace.Id)).Returns(true);
        held = false;
        await Task.Delay(80);
        Assert.Empty(Sent());

        queue.Setup(x => x.IsStarting(workspace.Id)).Returns(false);

        await Until(() => Sent().Count == 1);
    }

    [Fact]
    public async Task AQueuedInput_WaitsWhileWorkSomeoneElseSendsIsStillOnItsWaySoThatWorkIsStillFollowed()
    {
        await Open();
        WorkStep other = new() { Source = ActivitySource.BringIn, Text = "bring in" };
        TaskCompletionSource<WorkEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(followUps.TryExpect(workspace.Id, other, end => ended.TrySetResult(end)));

        // Its sender may take a while to get the workspace, which is still ready meanwhile.
        await Queue("next");
        await Task.Delay(100);

        Assert.Empty(Sent());
        Take("bring in", null);
        followUps.Sent(workspace.Id, other.Id);
        End();
        Assert.Equal(ActivityOutcome.Done, (await ended.Task.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        await Until(() => Sent().Count == 2);
        Assert.Equal(["bring in", "next"], Sent());
    }

    [Fact]
    public async Task SetAutoCommit_NeedsWorkUnderWayAndAllowsTheRunOfALoop()
    {
        await Open();
        Assert.Throws<RequestException>(() => followUps.SetAutoCommit(workspace.Id, true));
        loopRun = true;
        Send("run");

        followUps.SetAutoCommit(workspace.Id, true);

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task SetAutoCommit_AndAStopThatComesAtTheSameTimeDoNotLockEachOtherUp()
    {
        await Open();
        Send("work");
        using ManualResetEventSlim halting = new();
        using IDisposable watch = events.Subscribe(remoteEvent =>
        {
            if (remoteEvent.State == WorkspaceState.Stopping)
            {
                halting.Set();
                Thread.Sleep(100);
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
    public async Task Abort_TurnsOffWhatWasTurnedOnForWorkThatNeverStarted()
    {
        await Open();
        Send("work");
        followUps.SetAutoCommit(workspace.Id, true);

        followUps.Abort(workspace.Id);

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
    }

    [Fact]
    public async Task RestoredWorkThatIsToBeCommitted_HasItsCommitOnAgain()
    {
        restored.Add(workspace.Id);
        queue.Setup(x => x.Get(workspace.Id)).Returns(new QueueStatus { Position = 1 });

        await Open();

        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
    }

    private async Task Earlier(StoredAutomation state)
    {
        AutomationStore earlier = new();
        await earlier.Open(temp.Path, CancellationToken.None);
        earlier.Save(() => new Dictionary<Guid, StoredAutomation> { [workspace.Id] = state });
        await earlier.Flush();
    }

    [Fact]
    public async Task AfterARestart_WorkThatWaitedForTheUsageLimitIsGoneOnWithRatherThanCommitted()
    {
        await Open();
        await Queue("half done");
        await Until(() => Sent().Count == 1);
        suspended = true;
        End();
        await Task.Delay(50);
        await automation.Flush();
        await stopping.CancelAsync();
        suspended = false;
        using CancellationTokenSource again = new();
        followUps = Create();

        await followUps.Open(temp.Path, again.Token);

        await Until(() => Sent().Count == 2);
        Assert.Equal(("half done", 0), (Sent()[1], Commits()));
        await again.CancelAsync();
    }

    [Fact]
    public async Task ARetryWhoseSendFailsAfterTheAiBeganOnIt_IsNotLost()
    {
        await Open();
        int sends = 0;
        prompts.Setup(x => x.PromptWhenIdle(workspace.Id, It.IsAny<string>(), It.IsAny<Action>(), It.IsAny<CancellationToken>())).Returns((Guid _, string text, Action taken, CancellationToken _) =>
        {
            Take(text, taken);

            // The first retry begins and then cannot reach the tool.
            if (Interlocked.Increment(ref sends) == 2)
            {
                End(answered: false, error: "The network is unreachable.");
                return Task.FromException(new RequestException("The network is unreachable."));
            }

            return Task.CompletedTask;
        });
        await Queue("keep me");
        await Until(() => Sent().Count == 1);

        End(answered: false, error: "The network is unreachable.");

        await Until(() => Sent().Count == 3);
        Assert.All(Sent(), text => Assert.Equal("keep me", text));
    }

    [Fact]
    public async Task WaitingWorkThatIsTakenBack_LeavesNoCommitToHoldEverythingElseBack()
    {
        await Open();
        WorkStep run = new() { Source = ActivitySource.Loop, Text = "review" };
        followUps.Expect(workspace.Id, run);
        followUps.Sent(workspace.Id, run.Id);
        queue.Setup(x => x.Get(workspace.Id)).Returns(new QueueStatus { Position = 1 });
        await Queue("next");
        Assert.Equal(AutoCommitState.On, followUps.Get(workspace.Id));
        queue.Setup(x => x.Get(workspace.Id)).Returns((QueueStatus?)null);

        // The loop gives up its place for the queued input.
        followUps.NotSent(workspace.Id, run.Id);

        await Until(() => Sent().Count == 1);
    }

    [Fact]
    public async Task AUsersMessageThatGetsThereFirst_IsNotTakenForTheExpectedWork()
    {
        await Open();
        WorkStep run = new() { Source = ActivitySource.Loop, Text = "review everything", Commit = true };
        followUps.Expect(workspace.Id, run);

        Send("a question of mine");

        Assert.Equal(AutoCommitState.Off, followUps.Get(workspace.Id));
        Assert.Equal(string.Empty, briefs[0]);
        Assert.Equal(ActivitySource.Send, followUps.Describe(workspace.Id).Step!.Source);
    }

    [Fact]
    public async Task AfterARestart_WorkThatWaitedToStartAndIsNotInTheLocalQueueAnyMoreIsSent()
    {
        await Earlier(new StoredAutomation { Step = new WorkStep { Source = ActivitySource.Queue, Text = "was launching", Commit = true }, IsArmed = true });

        await Open();

        await Until(() => Sent().Count == 1);
        Assert.Equal("was launching", Sent()[0]);
    }

    [Fact]
    public async Task AfterARestart_AWaitingRetryAndTheWorkThatWasUnderWayAreBothDone()
    {
        await Earlier(new StoredAutomation
        {
            Retry = new WorkStep { Source = ActivitySource.Queue, Text = "waiting", Preface = "again" },
            RetryAt = DateTimeOffset.UtcNow,
            Step = new WorkStep { Source = ActivitySource.Send, Text = "under way", Started = true }
        });

        await Open();

        await Until(() => Sent().Count == 1);
        End();
        await Until(() => Sent().Count == 2);
        Assert.Equal(["waiting", "under way"], Sent());
    }

    [Fact]
    public async Task StopRetries_GivesUpOnWhatWaitsToBeTriedAgainAndHoldsTheQueue()
    {
        await Earlier(new StoredAutomation { Retry = new WorkStep { Source = ActivitySource.Queue, Text = "hopeless", Preface = "again" }, RetryAt = DateTimeOffset.UtcNow.AddHours(1) });
        await Open();
        await Queue("next");

        Assert.True(followUps.StopRetries(workspace.Id));

        Assert.False(followUps.StopRetries(workspace.Id));
        Assert.True(followUps.IsStopped(workspace.Id));
        followUps.Resume(workspace.Id);
        await Until(() => Sent().Count == 1);
        Assert.Equal(["next"], Sent());
    }

    [Fact]
    public async Task QueuingDoesNotTurnTheCommitBackOnForARetryTheUserTurnedItOffFor()
    {
        await Earlier(new StoredAutomation { Retry = new WorkStep { Source = ActivitySource.Queue, Text = "again", Preface = "p", Commit = true }, RetryAt = DateTimeOffset.UtcNow.AddHours(1) });
        await Open();
        followUps.SetAutoCommit(workspace.Id, false);

        await Queue("next");

        Assert.False(followUps.Describe(workspace.Id).Retry!.Commit);
    }

    [Fact]
    public async Task WhatTheServerDoesWhileItShutsDown_IsNotKeptSoTheWorkGoesOnAfterwards()
    {
        await Open();
        await Queue("long job");
        await Until(() => Sent().Count == 1);

        await stopping.CancelAsync();
        // Closing the AI sessions ends their work.
        End(stopped: true);
        await automation.Flush();

        AutomationStore reader = new();
        StoredAutomation kept = (await reader.Open(temp.Path, CancellationToken.None))[workspace.Id];
        Assert.Equal("long job", kept.Step?.Text);
        Assert.False(kept.IsStopped);
    }

    [Fact]
    public async Task AQueueThatAnOlderReleaseHadPaused_StaysHeldUntilResumed()
    {
        inputs = [new SavedInput { Id = Guid.NewGuid(), Text = "held back", Time = DateTimeOffset.UtcNow, IsQueued = true, WasPaused = true }];

        await Open();
        await Task.Delay(80);

        Assert.True(followUps.IsStopped(workspace.Id));
        Assert.Empty(Sent());
    }

    [Theory]
    [InlineData("You have hit your usage limit.", true)]
    [InlineData("API Error: 529 overloaded_error", true)]
    [InlineData("fetch failed: ECONNREFUSED", true)]
    [InlineData("Please log in again", true)]
    [InlineData("Claude Code could not be started: not found", true)]
    [InlineData("The prompt is too long for the model.", false)]
    [InlineData("The AI ended without a reply.", false)]
    public void FailureKinds_TellWhatPassesByItself(string error, bool passing) => Assert.Equal(passing, new FailureKinds().IsPassing(error));
}
