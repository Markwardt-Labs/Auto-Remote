namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceLoopsTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly Mock<IUsageService> usage = new();

    private readonly FakePromptProvider prompter = new();

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
        workspaces.Setup(x => x.AttachAiSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        subscription = hub.Subscribe(remoteEvent =>
        {
            lock (events)
            {
                events.Add(remoteEvent);
            }
        });
        activity = new WorkspaceActivity(hub);
        prompts = new PromptService(workspaces.Object, store.Object, (_, _) => prompter, hub, activity, snapshots.Object);
        prompts.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(Status(10));
    }

    public void Dispose()
    {
        stopping.Cancel();
        subscription.Dispose();
        temp.Dispose();
    }

    private FakePromptSession Session => prompter.Sessions[^1];

    private WorkspaceLoops Create(TimeSpan? windowInterval = null) => new(workspaces.Object, prompts, activity, usage.Object, hub, TimeSpan.FromMilliseconds(10), windowInterval ?? TimeSpan.FromMilliseconds(30));

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

    private string? Ended() => Changes().LastOrDefault(change => change.Loop is null)?.Text;

    [Fact]
    public async Task Start_SendsTheRunsOneAfterAnotherPublishesTheirCountAndEndsWithTheReason()
    {
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "  again  ", Condition = LoopCondition.Runs, Runs = 3 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);

        Assert.Equal(["again", "again", "again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        Assert.Equal([0, 1, 2, 3], Changes().Where(change => change.Loop is not null).Select(change => change.Loop!.Runs).ToList());
        Assert.Equal("The loop made all its runs.", Ended());
        Assert.False(prompts.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
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

    [Theory]
    [InlineData(WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing)]
    public async Task Start_RefusesAWorkspaceThatIsBusyWithSomethingOtherThanTheAi(WorkspaceState busy)
    {
        WorkspaceLoops loops = Create();
        activity.TryEnter(workspace.Id, busy);

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
    public async Task Start_StopsTheRunInProgressWhenTheTimeIsUp()
    {
        prompter.OnCreate = session => session.Hang();
        WorkspaceLoops loops = Create();

        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.ForDuration, DurationSeconds = 1 }, CancellationToken.None);
        await Until(() => loops.Get(workspace.Id) is null);
        await Until(() => !prompts.IsBusy(workspace.Id));

        Assert.Equal("The loop ran for the time it was given.", Ended());
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
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
    public async Task Start_StopsTheRunInProgressWhenTheWindowReachesItsShare()
    {
        prompter.OnCreate = session => session.Hang();
        double used = 10;
        usage.Setup(x => x.Get(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Status(used));
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", Condition = LoopCondition.UntilWindow, Window = "5 hours", Percent = 50 }, CancellationToken.None);
        await Until(() => prompts.IsBusy(workspace.Id));

        used = 60;
        await Until(() => loops.Get(workspace.Id) is null);
        await Until(() => !prompts.IsBusy(workspace.Id));

        Assert.StartsWith("5 hours reached 50%", Ended());
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
    public async Task Start_WaitsBetweenRunsWhileAScriptHoldsTheWorkspace()
    {
        WorkspaceLoops loops = Create();
        await loops.Start(workspace.Id, new LoopSettings { Prompt = "go", IntervalSeconds = 1, Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1 && !prompts.IsBusy(workspace.Id) && activity.Get(workspace.Id) == WorkspaceState.Ready);
        Assert.True(activity.TryEnter(workspace.Id, WorkspaceState.Running));

        await Task.Delay(1400);

        Assert.Single(Session.Prompts);

        activity.Leave(workspace.Id, WorkspaceState.Running);
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

    private IEnumerable<PromptEvent> Slow(Task gate, string text)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptMessageEvent { Text = text }];
    }
}
