namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceLoopsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly Mock<IUsageService> usage = new();

    private readonly FakePrompter prompter = new();

    private readonly HostMock host;

    private readonly EventHub hub = new();

    private readonly List<RemoteEvent> events = [];

    private readonly IDisposable subscription;

    private readonly WorkspaceActivity activity;

    private readonly PromptService prompts;

    private readonly CancellationTokenSource stopping = new();

    private readonly WorkspaceInfo workspace;

    public WorkspaceLoopsTests()
    {
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = temp.Path };
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => workspace);
        workspaces.Setup(x => x.AttachSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        subscription = hub.Subscribe(remoteEvent =>
        {
            lock (events)
            {
                events.Add(remoteEvent);
            }
        });
        activity = new WorkspaceActivity(hub);
        host = new HostMock(prompter);
        prompts = new PromptService(workspaces.Object, store.Object, host.Object, hub, activity, snapshots.Object);
        prompts.Configure(stopping.Token);
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(10));
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(10));
    }

    public void Dispose()
    {
        stopping.Cancel();
        subscription.Dispose();
        temp.Dispose();
    }

    private FakePromptSession Session => prompter.Sessions[^1];

    private WorkspaceLoops Create(TimeSpan? windowInterval = null, TimeSpan? limitInterval = null, IWindowGuard? guard = null) => new(workspaces.Object, prompts, activity, usage.Object, hub, TimeSpan.FromMilliseconds(10), windowInterval ?? TimeSpan.FromMilliseconds(30), limitInterval, guard);

    private UsageStatus Status(double percent) => new() { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "5 hours", PercentUsed = percent, WindowMinutes = 300 }] };

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private List<RemoteEvent> Changes()
    {
        lock (events)
        {
            return [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.LoopChanged)];
        }
    }

    // A loop is gone a moment before its end is announced.
    private string? Ended()
    {
        SpinWait.SpinUntil(() => Changes().Any(change => change.Loop is null), TimeSpan.FromSeconds(5));
        return Changes().LastOrDefault(change => change.Loop is null)?.Text;
    }

    [Fact]
    public async Task Start_SendsTheRunsOneAfterAnotherPublishesTheirCountAndEndsWithTheReason()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "  again  ", Condition = LoopCondition.Runs, Runs = 3 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["again", "again", "again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        // Each run is said to be under way before its prompt is sent, then counted, and said to be over before the next one.
        Assert.Equal([(0, false), (0, true), (1, true), (1, false), (1, true), (2, true), (2, false), (2, true), (3, true)], Changes().Where(change => change.Loop is not null).Select(change => (change.Loop!.Runs, change.Loop.IsRunning)).ToList());
        Assert.Equal("The loop made all its runs.", Ended());
        Assert.False(prompts.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Start_SendsNothingWhileTheWorkspaceIsPausedBecauseOfAUsageWindowAndGoesOnOnceThePauseEnds()
    {
        bool paused = true;
        Mock<IWindowGuard> guard = new();
        guard.Setup(x => x.IsPaused(workspace.Id)).Returns(() => paused);
        WorkspaceLoops loops = Create(guard: guard.Object);

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "again", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(prompter.Sessions);

        paused = false;
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Start_AsksTheGuardBeforeEveryPromptAndSendsNothingWhileItHoldsTheWorkspaceBecauseAUsageWindowIsAtTheMaximum()
    {
        bool hold = true;
        Mock<IWindowGuard> guard = new();
        guard.Setup(x => x.Hold(workspace.Id, It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => hold);
        WorkspaceLoops loops = Create(guard: guard.Object);

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "again", Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Task.Delay(300);
        Assert.Empty(prompter.Sessions);
        Assert.NotNull(loops.Get(workspace.Id));

        hold = false;
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["again", "again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        guard.Verify(x => x.Hold(workspace.Id, It.IsAny<AiService>(), It.IsAny<CancellationToken>()), Times.AtLeast(3));
    }

    [Fact]
    public async Task Start_WaitsTheIntervalFromTheStartOfEachRun()
    {
        WorkspaceLoops loops = Create();
        DateTime begin = DateTime.UtcNow;

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "again", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.True(DateTime.UtcNow - begin >= TimeSpan.FromMilliseconds(950));
        Assert.Equal(2, Session.Prompts.Count);
    }

    [Theory]
    [InlineData("", 0, "The loop's prompt is empty.")]
    [InlineData("   ", 0, "The loop's prompt is empty.")]
    [InlineData("go", -1, "The interval must be between 0 seconds and 24 hours.")]
    [InlineData("go", 86401, "The interval must be between 0 seconds and 24 hours.")]
    public async Task Start_RefusesSettingsThatAreNotValid(string prompt, int interval, string message)
    {
        WorkspaceLoops loops = Create();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = prompt, IntervalSeconds = interval }, CancellationToken.None));

        Assert.Equal(message, exception.Message);
        Assert.Null(loops.Get(workspace.Id));
    }

    [Theory]
    [InlineData(LoopCondition.Runs, null, null, "Choose how many runs the loop makes.")]
    [InlineData(LoopCondition.Runs, 0, null, "Choose how many runs the loop makes.")]
    [InlineData(LoopCondition.UntilTime, null, null, "Choose when the loop ends.")]
    [InlineData(LoopCondition.ForDuration, null, null, "Choose how long the loop runs.")]
    [InlineData(LoopCondition.ForDuration, null, 0, "The loop must run for a time between a second and 24 days.")]
    [InlineData(LoopCondition.ForDuration, null, 3000000, "The loop must run for a time between a second and 24 days.")]
    [InlineData(LoopCondition.UntilWindow, null, null, "Choose the usage window to watch.")]
    public async Task Start_RefusesAConditionThatIsNotComplete(LoopCondition condition, int? runs, int? duration, string message)
    {
        WorkspaceLoops loops = Create();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = condition, Runs = runs, DurationSeconds = duration }, CancellationToken.None));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task Start_RefusesAnEndTimeInThePastOrTooFarAhead()
    {
        WorkspaceLoops loops = Create();

        await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilTime, Until = DateTimeOffset.UtcNow.AddMinutes(-1) }, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilTime, Until = DateTimeOffset.UtcNow.AddDays(30) }, CancellationToken.None));
    }

    [Theory]
    [InlineData(null, 50.0, "Choose the usage window to watch.")]
    [InlineData("5 hours", null, "The share of the usage limit must be between 1 and 100.")]
    [InlineData("5 hours", 0.0, "The share of the usage limit must be between 1 and 100.")]
    [InlineData("5 hours", 101.0, "The share of the usage limit must be between 1 and 100.")]
    [InlineData("Weekly", 50.0, "Claude Code reports no usage window named Weekly.")]
    public async Task Start_RefusesAWindowConditionThatCannotBeWatched(string? window, double? percent, string message)
    {
        WorkspaceLoops loops = Create();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilWindow, Window = window, Percent = percent }, CancellationToken.None));

        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public async Task Start_RefusesASecondLoopInTheSameWorkspace()
    {
        prompter.OnCreate = session => session.Hang();
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None));

        Assert.Equal("A loop already runs in this workspace.", exception.Message);
        await loops.Cancel(workspace.Id);
    }

    [Fact]
    public async Task Start_RefusesAWorkspaceThatIsBusyWithSomethingOtherThanTheAi()
    {
        WorkspaceLoops loops = Create();
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);

        await Assert.ThrowsAsync<RequestException>(() => loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None));

        Assert.Null(loops.Get(workspace.Id));
    }

    [Fact]
    public async Task Get_DescribesTheRunningLoop()
    {
        prompter.OnCreate = session => session.Hang();
        WorkspaceLoops loops = Create();
        LoopSettings settings = new() { Prompt = "go", IntervalSeconds = 5, Condition = LoopCondition.ForDuration, DurationSeconds = 600 };

        await loops.Start(workspace.Id, settings, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id)?.Runs == 1);

        LoopInfo info = loops.Get(workspace.Id)!;
        Assert.Equal(settings, info.Settings);
        Assert.Equal(1, info.Runs);
        await loops.Cancel(workspace.Id);
    }

    [Fact]
    public async Task Start_EndsWhenTheTimeItWasGivenIsUp()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 60, Condition = LoopCondition.ForDuration, DurationSeconds = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop ran for the time it was given.", Ended());
    }

    [Fact]
    public async Task Start_EndsAtTheChosenTime()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 60, Condition = LoopCondition.UntilTime, Until = DateTimeOffset.UtcNow.AddSeconds(1) }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop reached its end time.", Ended());
    }

    [Fact]
    public async Task Start_NeverCutsOffTheRunInProgressWhenTheTimeIsUpAndOnlyStopsSendingMore()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => Slow(release.Task, "done"));
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.ForDuration, DurationSeconds = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);
        await Task.Delay(300);

        Assert.Equal("The loop ran for the time it was given.", Ended());
        Assert.True(prompts.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Working, activity.Get(workspace.Id));

        release.SetResult();
        await Until(() => !prompts.IsBusy(workspace.Id));

        Assert.Single(Session.Prompts);
        Assert.Equal(["done"], Messages());
    }

    [Fact]
    public async Task Start_EndsBeforeTheNextRunWhenTheWindowHasReachedItsShare()
    {
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Status(prompter.Sessions.Count > 0 && Session.Prompts.Count >= 2 ? 80 : 10));
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.UntilWindow, Window = "5 hours", Percent = 75 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(2, Session.Prompts.Count);
        Assert.Equal("5 hours reached 75% of its limit, so the loop ended.", Ended());
    }

    [Fact]
    public async Task Start_NeverCutsOffTheRunInProgressWhenTheWindowReachesItsShare()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => Slow(release.Task, "done"));
        double used = 10;
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Status(used));
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilWindow, Window = "5 hours", Percent = 50 }, CancellationToken.None);
        await Until(() => prompts.IsBusy(workspace.Id));

        used = 60;
        await Until(() => loops.Get(workspace.Id) is null);
        await Task.Delay(300);

        Assert.StartsWith("5 hours reached 50%", Ended());
        Assert.True(prompts.IsBusy(workspace.Id));

        release.SetResult();
        await Until(() => !prompts.IsBusy(workspace.Id));

        Assert.Single(Session.Prompts);
        Assert.Equal(["done"], Messages());
    }

    [Fact]
    public async Task Start_KeepsGoingWhenTheUsageCannotBeRead()
    {
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilWindow, Window = "5 hours", Percent = 50 }, CancellationToken.None);
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));

        await Until(() => Session.Prompts.Count >= 3);

        Assert.NotNull(loops.Get(workspace.Id));
        await loops.Cancel(workspace.Id);
    }

    [Fact]
    public async Task Cancel_LetsTheRunInProgressFinishAndStartsNoOther()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => Slow(release.Task, "done"));
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await Until(() => prompts.IsBusy(workspace.Id));

        await loops.Cancel(workspace.Id);

        Assert.Null(loops.Get(workspace.Id));
        Assert.True(prompts.IsBusy(workspace.Id));
        Assert.Null(Ended());
        Assert.Contains(Changes(), change => change.Loop is null && change.Text is null);

        release.SetResult();
        await Until(() => !prompts.IsBusy(workspace.Id));
        await Task.Delay(150);

        Assert.Single(Session.Prompts);
    }

    [Fact]
    public async Task Cancel_WithoutALoopDoesNothing()
    {
        WorkspaceLoops loops = Create();

        await loops.Cancel(workspace.Id);

        Assert.Empty(Changes());
    }

    [Fact]
    public async Task Start_AfterACancelStartsAFreshLoop()
    {
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await loops.Cancel(workspace.Id);

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "second", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null && Session.Prompts.Any(prompt => prompt.Text == "second"));

        Assert.Equal("The loop made all its runs.", Ended());
    }

    [Fact]
    public async Task Start_WaitsForTheAiToBeFreeBeforeTheFirstRun()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "busy" ? Slow(release.Task, "free") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        WorkspaceLoops loops = Create();
        await prompts.Prompt(workspace.Id, "busy", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Task.Delay(200);

        Assert.Single(Session.Prompts);

        release.SetResult();
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["busy", "go"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Start_WaitsBetweenRunsWhileAGitActionHoldsTheWorkspace()
    {
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1 && !prompts.IsBusy(workspace.Id) && activity.Get(workspace.Id) == WorkspaceState.Ready);
        Assert.True(activity.TryEnter(workspace.Id, WorkspaceState.Managing));

        await Task.Delay(1400);

        Assert.Single(Session.Prompts);

        activity.Leave(workspace.Id, WorkspaceState.Managing);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(2, Session.Prompts.Count);
    }

    [Fact]
    public async Task Start_SteersAPromptSentDuringARunIntoItAndFollowsItUpBeforeTheNextRun()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "go" && release.Task.IsCompleted is false ? Slow(release.Task, "first run") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);

        await prompts.Prompt(workspace.Id, "also this", CancellationToken.None);
        await Until(() => Session.Prompts[0].Steered.Count == 1);
        release.SetResult();
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["go", "also this", "go"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Start_NeverGivesThePromptWhileTheAiAlreadyWorksNorBeforeTheIntervalPassed()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "own" ? Slow(release.Task, "own reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1 && !prompts.IsBusy(workspace.Id));
        await prompts.Prompt(workspace.Id, "own", CancellationToken.None);

        await Task.Delay(1400);

        Assert.Equal(["go", "own"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        Assert.Empty(Session.Prompts.SelectMany(prompt => prompt.Steered));

        release.SetResult();
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["go", "own", "go"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Get_ReportsWhenTheNextPromptIsDueOnceARunStarted()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 60 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id)?.Runs == 1);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        LoopInfo info = loops.Get(workspace.Id)!;
        Assert.Null(Changes().First(change => change.Loop is not null).Loop!.NextRunAt);
        Assert.InRange(info.NextRunAt!.Value, after.AddSeconds(59) - TimeSpan.FromSeconds(5), after.AddSeconds(61));
        Assert.Equal(info.NextRunAt, Changes().Last(change => change.Loop is not null).Loop!.NextRunAt);
        await loops.Cancel(workspace.Id);
    }

    [Fact]
    public async Task Start_HoldsTheSessionOpenWhileTheLoopRunsAndReleasesItAfterwards()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => prompter.Sessions.Count == 1 && Session.Prompts.Count == 1);
        await Until(() => !prompts.IsBusy(workspace.Id));

        Assert.False(Session.IsDisposed);

        await Until(() => loops.Get(workspace.Id) is null);

        Assert.False(Session.IsDisposed);
        Assert.False(prompts.IsBusy(workspace.Id));
    }

    [Fact]
    public async Task Start_EndsWithTheFailureWhenARunFails()
    {
        prompter.OnCreate = session => session.Fail("boom");
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop stopped: boom", Ended());
        Assert.Single(Session.Prompts);
    }

    [Fact]
    public async Task Start_EndsWithTheReasonWhenARunIsRefusedAndNothingIsWorking()
    {
        int reads = 0;
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => Interlocked.Increment(ref reads) == 1 ? workspace : throw new RequestException("gone"));
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop stopped: gone", Ended());
        Assert.Empty(prompter.Sessions);
    }

    [Fact]
    public async Task Cancel_FollowedByAnInterruptLeavesNoLoopAndNoRun()
    {
        prompter.OnCreate = session => session.Hang().Reply("fine");
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await Until(() => prompts.IsBusy(workspace.Id));

        await loops.Cancel(workspace.Id);
        await prompts.Interrupt(workspace.Id);
        await Task.Delay(150);

        Assert.Null(loops.Get(workspace.Id));
        Assert.False(prompts.IsBusy(workspace.Id));
        Assert.Single(Session.Prompts);
    }

    private List<string> Messages()
    {
        lock (events)
        {
            return [.. events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message!.Role == MessageRole.Assistant).Select(remoteEvent => remoteEvent.Message!.Text)];
        }
    }

    private IEnumerable<PromptEvent> Slow(Task gate, string text)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptMessageEvent { Text = text }];
    }

    private int failures;

    private void FailFirst(string message, int times = 1) => prompter.OnCreate = session => session.ReplyWith(prompt => Interlocked.Increment(ref failures) <= times ? [new PromptFailureEvent { Message = message }] : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);

    private bool Waiting() => Changes().Any(change => change.Loop?.LimitCheckAt is not null);

    [Fact]
    public async Task Start_WaitsForTheUsageLimitInsteadOfEndingChecksAgainAndGoesOnOnceItIsFree()
    {
        FailFirst("5-hour limit reached, resets at 9pm");
        int reads = 0;
        double used = 100;
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Interlocked.Increment(ref reads);
            return Status(used);
        });
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(100));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(Waiting);
        await Task.Delay(450);

        Assert.NotNull(loops.Get(workspace.Id));
        Assert.NotNull(loops.Get(workspace.Id)!.LimitCheckAt);
        Assert.Single(Session.Prompts);
        Assert.True(reads >= 4);
        Assert.Equal(0, loops.Get(workspace.Id)!.Runs);
        Assert.DoesNotContain(Changes(), change => change.Loop is null);

        used = 40;
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(3, Session.Prompts.Count);
        Assert.Equal("The loop made all its runs.", Ended());
        Assert.Null(Changes().Where(change => change.Loop is not null).Last().Loop!.LimitCheckAt);
        Assert.Equal(["reply to go", "reply to go"], Messages());
    }

    [Fact]
    public async Task Start_GoesOnToPromptAgainWhenTheUsageCannotBeReadWhileItWaits()
    {
        FailFirst("You've hit your usage limit. Try again later.");
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync((UsageStatus?)null);
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(100));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.True(Waiting());
        Assert.Equal(2, Session.Prompts.Count);
        Assert.Equal("The loop made all its runs.", Ended());
    }

    [Fact]
    public async Task Start_TriesAgainAndWaitsAgainWhenTheLimitIsStillThere()
    {
        FailFirst("Rate limit exceeded", 2);
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync((UsageStatus?)null);
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(80));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(3, Session.Prompts.Count);
        Assert.Equal("The loop made all its runs.", Ended());
        Assert.True(Changes().Count(change => change.Loop?.LimitCheckAt is not null) >= 2);
    }

    [Fact]
    public async Task Start_WaitsWhenAWindowIsFullEvenIfTheFailureDoesNotSayLimit()
    {
        FailFirst("The request failed");
        double used = 100;
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Status(used));
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(100));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.Runs, Runs = 1 }, CancellationToken.None);
        await Until(Waiting);

        Assert.NotNull(loops.Get(workspace.Id));

        used = 70;
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop made all its runs.", Ended());
    }

    [Fact]
    public async Task Start_StillEndsWithTheFailureWhenItHasNothingToDoWithTheLimit()
    {
        FailFirst("The tool crashed");
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(50));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop stopped: The tool crashed", Ended());
        Assert.False(Waiting());
        Assert.Single(Session.Prompts);
    }

    [Theory]
    [InlineData("5-hour limit reached, resets at 9pm", 100)]
    [InlineData("The request failed", 100)]
    public async Task Start_EndsLikeOnAnyOtherFailureWhenItWasStartedWithoutWaitingForTheLimit(string failure, double used)
    {
        FailFirst(failure);
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(used));
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(50));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", WaitForLimit = false }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal($"The loop stopped: {failure}", Ended());
        Assert.False(Waiting());
        Assert.Single(Session.Prompts);
        usage.Verify(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_EndsALoopThatWaitsForTheLimitAtOnce()
    {
        FailFirst("usage limit reached");
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(100));
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMilliseconds(60));
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go" }, CancellationToken.None);
        await Until(Waiting);

        await loops.Cancel(workspace.Id);
        await Task.Delay(250);

        Assert.Null(loops.Get(workspace.Id));
        Assert.Single(Session.Prompts);
        Assert.False(prompts.IsBusy(workspace.Id));
    }

    [Fact]
    public async Task Start_EndsAWaitingLoopWhenItsTimeIsUp()
    {
        FailFirst("usage limit reached");
        usage.Setup(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(100));
        WorkspaceLoops loops = Create(limitInterval: TimeSpan.FromMinutes(10));

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.ForDuration, DurationSeconds = 1 }, CancellationToken.None);
        await Until(Waiting);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal("The loop ran for the time it was given.", Ended());
        Assert.Single(Session.Prompts);
    }
}
