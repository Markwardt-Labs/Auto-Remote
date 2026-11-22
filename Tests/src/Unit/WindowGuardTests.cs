namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WindowGuardTests : IDisposable
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IUsageService> usage = new();

    private readonly Mock<IPrompterHost> host = new();

    private readonly EventHub hub = new();

    private readonly ManualTime time = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly List<RemoteEvent> published = [];

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.ClaudeCode };

    private readonly WindowGuard guard;

    private bool busy = true;

    private UsageStatus? reading;

    private UsageStatus? fresh;

    public WindowGuardTests()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        host.Setup(x => x.Get()).Returns(new AiEnvironment());
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(() => busy);
        prompts.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        usage.Setup(x => x.Get(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(() => reading!);
        usage.Setup(x => x.Refresh(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(() => fresh);
        hub.Subscribe(published.Add);
        guard = new WindowGuard(workspaces.Object, prompts.Object, usage.Object, host.Object, hub, time) { CheckEvery = TimeSpan.FromDays(1) };
        guard.Open(stopping.Token);
    }

    public void Dispose() => stopping.Cancel();

    private UsageStatus Status(double five, double week = 10) => new() { Service = AiService.ClaudeCode, Windows = [new UsagePeriod { Name = "5h", PercentUsed = five, WindowMinutes = 300 }, new UsagePeriod { Name = "7d", PercentUsed = week, WindowMinutes = 10080 }] };

    private void Works(bool working = true) => hub.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working });

    private void Said(string text) => hub.Publish(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Message = new ChatMessage { Role = MessageRole.User, Text = text } });

    private async Task Paused(double percent = 96)
    {
        reading = Status(percent);
        Works();
        await guard.Sweep(CancellationToken.None);
    }

    private IEnumerable<LimitPause?> Changes() => published.Where(item => item.Kind == RemoteEventKind.LimitPauseChanged).Select(item => item.LimitPause);

    [Fact]
    public async Task Sweep_TellsTheAiToPauseWhenAWindowIsAtTheMaximumWhileItWorks()
    {
        await Paused(96);

        prompts.Verify(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>()), Times.Once);
        LimitPause pause = guard.Get(workspace.Id)!;
        Assert.Equal(("5h", 96d, 95d), (pause.Window, pause.PercentUsed, pause.MaxPercent));
        Assert.Equal((time.GetUtcNow(), time.GetUtcNow().AddMinutes(15)), (pause.PausedAt, pause.NextCheckAt));
        Assert.True(guard.IsPaused(workspace.Id));
        Assert.Equal([pause], Changes());
    }

    [Theory]
    [InlineData(95, true)]
    [InlineData(94.9, false)]
    public async Task Sweep_PausesFromExactlyTheMaximumOn(double percent, bool paused)
    {
        await Paused(percent);

        Assert.Equal(paused, guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Sweep_UsesTheMaximumOfTheProviderOfTheWorkspaceAndTheFullestWindowThatIsPastIt()
    {
        host.Setup(x => x.Get()).Returns(new AiEnvironment { ClaudeMaxWindowPercent = 70, CodexMaxWindowPercent = 99 });
        reading = Status(71, 80);
        Works();

        await guard.Sweep(CancellationToken.None);

        Assert.Equal(("7d", 80d, 70d), (guard.Get(workspace.Id)!.Window, guard.Get(workspace.Id)!.PercentUsed, guard.Get(workspace.Id)!.MaxPercent));
    }

    [Fact]
    public async Task Sweep_HoldsEachProviderToItsOwnMaximum()
    {
        host.Setup(x => x.Get()).Returns(new AiEnvironment { ClaudeMaxWindowPercent = 99, CodexMaxWindowPercent = 80 });
        WorkspaceInfo codex = workspace with { Id = Guid.NewGuid(), Service = AiService.Codex };
        workspaces.Setup(x => x.Get(codex.Id, It.IsAny<CancellationToken>())).ReturnsAsync(codex);
        prompts.Setup(x => x.Prompt(codex.Id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        reading = Status(90);
        usage.Setup(x => x.Get(AiService.Codex, It.IsAny<CancellationToken>())).ReturnsAsync(Status(90));
        Works();
        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = codex.Id, Working = true });

        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        Assert.True(guard.IsPaused(codex.Id));
        Assert.Equal(80d, guard.Get(codex.Id)!.MaxPercent);
    }

    [Fact]
    public async Task AWorkspaceOfAProviderThatReportsNoUsageIsNeverReadOrHeld()
    {
        WorkspaceInfo local = workspace with { Id = Guid.NewGuid(), Service = AiService.Local };
        workspaces.Setup(x => x.Get(local.Id, It.IsAny<CancellationToken>())).ReturnsAsync(local);
        usage.Setup(x => x.Get(AiService.Local, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.Local, Windows = [new UsagePeriod { Name = "odd", PercentUsed = 100, WindowMinutes = 60 }] });
        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = local.Id, Working = true });

        await guard.Sweep(CancellationToken.None);

        Assert.False(await guard.Hold(local.Id, AiService.Local, CancellationToken.None));
        Assert.Null(await guard.Over(local.Id, AiService.Local, "go", CancellationToken.None));
        Assert.False(guard.IsPaused(local.Id));
        usage.Verify(x => x.Get(AiService.Local, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hold_PausesAnIdleWorkspaceWithoutSendingAnythingWhenAWindowIsAtTheMaximumAndLetsItGoOnceTheyAreBelowIt()
    {
        busy = false;
        reading = Status(96);

        bool held = await guard.Hold(workspace.Id, AiService.ClaudeCode, CancellationToken.None);

        Assert.True(held);
        Assert.True(guard.IsPaused(workspace.Id));
        Assert.Equal(("5h", 96d, 95d), (guard.Get(workspace.Id)!.Window, guard.Get(workspace.Id)!.PercentUsed, guard.Get(workspace.Id)!.MaxPercent));
        Assert.NotNull(Changes().Single());
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(await guard.Hold(workspace.Id, AiService.ClaudeCode, CancellationToken.None));

        fresh = Status(96);
        time.Advance(TimeSpan.FromMinutes(15));
        await guard.Sweep(CancellationToken.None);
        Assert.True(guard.IsPaused(workspace.Id));

        fresh = Status(60);
        time.Advance(TimeSpan.FromMinutes(15));
        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        Assert.Null(Changes().Last());
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Hold_LeavesAWorkspaceAloneWhenNoWindowIsAtTheMaximumOrTheWindowsCannotBeRead()
    {
        reading = Status(94);
        Assert.False(await guard.Hold(workspace.Id, AiService.ClaudeCode, CancellationToken.None));

        usage.Setup(x => x.Get(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        Assert.False(await guard.Hold(workspace.Id, AiService.ClaudeCode, CancellationToken.None));

        Assert.False(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Sweep_LeavesAWorkspaceAloneWhoseAiDoesNotWorkOrWhoseProviderReportsNoWindows()
    {
        reading = Status(99);
        await guard.Sweep(CancellationToken.None);
        Works();
        Works(false);
        await guard.Sweep(CancellationToken.None);
        Works();
        reading = new UsageStatus { Service = AiService.ClaudeCode };
        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Sweep_PausesOnlyOnceAndReadsTheWindowsOncePerProvider()
    {
        await Paused();
        await guard.Sweep(CancellationToken.None);

        prompts.Verify(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Single(Changes());
    }

    [Fact]
    public async Task Sweep_DoesNotPauseWhenThePausePromptIsRefusedAndTriesAgainNextTime()
    {
        prompts.SetupSequence(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The workspace is stopping.")).Returns(Task.CompletedTask);

        await Paused();
        Assert.False(guard.IsPaused(workspace.Id));
        await guard.Sweep(CancellationToken.None);

        Assert.True(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Sweep_ReadsTheWindowsAgainOnlyWhenTheFifteenMinutesAreOverAndKeepsThePauseWhileTheyAreStillPastTheMaximum()
    {
        await Paused(96);
        fresh = Status(97);

        time.Advance(TimeSpan.FromMinutes(14));
        await guard.Sweep(CancellationToken.None);
        usage.Verify(x => x.Refresh(It.IsAny<AiService>(), It.IsAny<CancellationToken>()), Times.Never);

        time.Advance(TimeSpan.FromMinutes(1));
        await guard.Sweep(CancellationToken.None);

        usage.Verify(x => x.Refresh(AiService.ClaudeCode, It.IsAny<CancellationToken>()), Times.Once);
        LimitPause pause = guard.Get(workspace.Id)!;
        Assert.Equal((97d, time.GetUtcNow().AddMinutes(15)), (pause.PercentUsed, pause.NextCheckAt));
        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(2, Changes().Count());
    }

    [Fact]
    public async Task Sweep_TellsTheAiToResumeOnceNoWindowIsPastTheMaximumAndEndsThePause()
    {
        await Paused(96);
        busy = false;
        fresh = Status(60);
        time.Advance(TimeSpan.FromMinutes(15));

        await guard.Sweep(CancellationToken.None);

        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(guard.IsPaused(workspace.Id));
        Assert.Null(guard.Get(workspace.Id));
        Assert.Null(Changes().Last());
    }

    [Fact]
    public async Task Sweep_WaitsForTheAiToBeIdleBeforeTellingItToResumeWithoutReadingTheWindowsAgainMeanwhile()
    {
        await Paused(96);
        fresh = Status(60);
        time.Advance(TimeSpan.FromMinutes(15));

        await guard.Sweep(CancellationToken.None);

        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(guard.IsPaused(workspace.Id));

        busy = false;
        time.Advance(TimeSpan.FromDays(1));
        await guard.Sweep(CancellationToken.None);

        usage.Verify(x => x.Refresh(AiService.ClaudeCode, It.IsAny<CancellationToken>()), Times.Once);
        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Sweep_KeepsThePauseWhenTheWindowsCannotBeReadAndTriesTheResumeAgainWhenItIsRefused()
    {
        await Paused(96);
        busy = false;
        fresh = null;
        time.Advance(TimeSpan.FromMinutes(15));
        await guard.Sweep(CancellationToken.None);
        Assert.True(guard.IsPaused(workspace.Id));
        Assert.Equal(time.GetUtcNow().AddMinutes(15), guard.Get(workspace.Id)!.NextCheckAt);

        fresh = Status(10);
        prompts.SetupSequence(x => x.Prompt(workspace.Id, "resume unfinished work", true, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Git is running.")).Returns(Task.CompletedTask);
        time.Advance(TimeSpan.FromMinutes(15));
        await guard.Sweep(CancellationToken.None);
        Assert.True(guard.IsPaused(workspace.Id));

        time.Advance(TimeSpan.FromDays(1));
        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", true, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AMessageTheUserSendsDoesNotEndThePauseBecauseOnlyAConfirmedPromptLetsTheWorkspaceIgnoreTheMaximum()
    {
        await Paused();

        Said("pause current work");
        Said("resume unfinished work");
        Said("do something else");

        Assert.True(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Over_RefusesAPromptWhileAWindowIsAtTheMaximumShowingThePauseOrTheWindowButNeverThePausePrompt()
    {
        reading = Status(96);
        LimitPause fresh = (await guard.Over(workspace.Id, AiService.ClaudeCode, "go", CancellationToken.None))!;
        Assert.Equal(("5h", 96d, 95d), (fresh.Window, fresh.PercentUsed, fresh.MaxPercent));
        Assert.False(guard.IsPaused(workspace.Id));

        await Paused(97);
        LimitPause paused = (await guard.Over(workspace.Id, AiService.ClaudeCode, "go", CancellationToken.None))!;
        Assert.Equal(guard.Get(workspace.Id), paused);

        Assert.Null(await guard.Over(workspace.Id, AiService.ClaudeCode, "pause current work", CancellationToken.None));
        reading = Status(10);
        guard.Cancel(workspace.Id);
        Assert.Null(await guard.Over(workspace.Id, AiService.ClaudeCode, "go", CancellationToken.None));
    }

    [Fact]
    public async Task Exempt_EndsThePauseWithoutResumingAndKeepsTheAiFromBeingPausedUntilTheWorkIsDone()
    {
        await Paused(96);

        guard.Exempt(workspace.Id);

        Assert.False(guard.IsPaused(workspace.Id));
        Assert.Null(Changes().Last());
        Assert.Null(await guard.Over(workspace.Id, AiService.ClaudeCode, "go", CancellationToken.None));
        Assert.False(await guard.Hold(workspace.Id, AiService.ClaudeCode, CancellationToken.None));
        prompts.Verify(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>()), Times.Once);

        await guard.Sweep(CancellationToken.None);
        Assert.False(guard.IsPaused(workspace.Id));
        prompts.Verify(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>()), Times.Once);

        Works(false);
        Works();
        await guard.Sweep(CancellationToken.None);

        Assert.True(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Exempt_LastsUntilTheWorkTheNextPromptStartsIsDoneAndUnexemptTakesItBack()
    {
        reading = Status(99);
        guard.Exempt(workspace.Id);
        Works(false);
        Works();
        await guard.Sweep(CancellationToken.None);
        Assert.False(guard.IsPaused(workspace.Id));
        Works(false);
        Works();
        await guard.Sweep(CancellationToken.None);
        Assert.True(guard.IsPaused(workspace.Id));

        guard.Cancel(workspace.Id);
        guard.Exempt(workspace.Id);
        guard.Unexempt(workspace.Id);
        Works(false);
        Works();
        await guard.Sweep(CancellationToken.None);

        Assert.True(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task Cancel_EndsThePauseWithoutResumingAndDoesNotPauseTheAiAgainWhileItFinishesWhatItIsDoing()
    {
        await Paused(96);
        busy = false;
        fresh = Status(10);

        guard.Cancel(workspace.Id);
        time.Advance(TimeSpan.FromMinutes(30));
        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        Assert.Null(Changes().Last());
        prompts.Verify(x => x.Prompt(workspace.Id, "resume unfinished work", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        prompts.Verify(x => x.Prompt(workspace.Id, "pause current work", false, It.IsAny<CancellationToken>()), Times.Once);

        reading = Status(99);
        await guard.Sweep(CancellationToken.None);
        Assert.False(guard.IsPaused(workspace.Id));

        Works(false);
        Works();
        await guard.Sweep(CancellationToken.None);
        Assert.True(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task StoppingTheWorkEndsThePauseSoNothingResumesWhatTheUserStopped()
    {
        await Paused();

        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = WorkspaceState.Stopping });

        Assert.False(guard.IsPaused(workspace.Id));
    }

    [Fact]
    public async Task AWorkspaceThatIsGoneIsForgottenAndItsPauseEnds()
    {
        await Paused();
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("No such workspace."));

        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
        Assert.Null(Changes().Last());
    }

    [Fact]
    public async Task Sweep_SkipsAProviderWhoseWindowsCannotBeRead()
    {
        usage.Setup(x => x.Get(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("offline"));
        Works();

        await guard.Sweep(CancellationToken.None);

        Assert.False(guard.IsPaused(workspace.Id));
    }
}
