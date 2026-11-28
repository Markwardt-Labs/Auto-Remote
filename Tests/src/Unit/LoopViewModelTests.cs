namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class LoopViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly DateTimeOffset now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    public LoopViewModelTests() => connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it", GlobalExpandPrompt = "expand it" });

    private LoopViewModel Opened()
    {
        Windows();
        LoopViewModel panel = Panel();
        panel.Prepare(false).GetAwaiter().GetResult();
        return panel;
    }

    private LoopViewModel Panel() => new(connection.Object, workspace);

    private void Windows(params string[] names) => connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [.. names.Select(name => new UsagePeriod { Name = name, PercentUsed = 10, WindowMinutes = 300 })] });

    [Fact]
    public async Task Build_SendsTheReviewPromptOrTheUsersOwnAndRunsForeverBackToBackWhenAsked()
    {
        Windows();
        LoopViewModel panel = Panel();
        await panel.Prepare(false);
        panel.IntervalMinutes = 0;
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.Forever);

        LoopSettings review = panel.Build(now, string.Empty);
        panel.UseCustom = true;
        LoopSettings custom = panel.Build(now, "  check the logs  ");

        Assert.Equal(("review it", 0, LoopCondition.Forever), (review.Prompt, review.IntervalSeconds, review.Condition));
        Assert.Equal("check the logs", custom.Prompt);
        Assert.False(panel.UseReview);
    }

    [Fact]
    public async Task Build_SendsTheExpandPromptWhenExpandIsChosenAndTheChoicesExcludeEachOther()
    {
        LoopViewModel panel = Opened();
        List<string?> changed = [];
        panel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        panel.UseExpand = true;
        LoopSettings expand = panel.Build(now, string.Empty);
        bool afterExpand = panel.UseReview || panel.UseCustom;
        panel.UseExpand = false;
        bool ignoredFalse = panel.UseExpand;
        panel.UseCustom = true;
        panel.UseReview = true;
        panel.UseCustom = false;
        LoopSettings review = panel.Build(now, string.Empty);
        await panel.Prepare(false);

        Assert.Equal(("expand it", false, true), (expand.Prompt, afterExpand, ignoredFalse));
        Assert.Equal("review it", review.Prompt);
        Assert.Contains(nameof(LoopViewModel.UseExpand), changed);
        Assert.True(panel.UseReview);
    }

    [Fact]
    public async Task Prepare_ReadsTheReviewPromptOfTheWorkspaceOrTheGlobalOneAndBuildRefusesWhenItCouldNotBeRead()
    {
        Windows();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { ReviewPrompt = "  custom review ", ExpandPrompt = " custom expand ", GlobalReviewPrompt = "global review", GlobalExpandPrompt = "global expand" });
        LoopViewModel own = Panel();
        await own.Prepare(false);

        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "global review", GlobalExpandPrompt = "global expand" });
        LoopViewModel global = Panel();
        await global.Prepare(false);

        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        LoopViewModel unread = Panel();
        await unread.Prepare(false);

        Assert.Equal(("custom review", "global review", string.Empty), (own.ReviewPrompt, global.ReviewPrompt, unread.ReviewPrompt));
        Assert.Equal(("custom expand", "global expand", string.Empty), (own.ExpandPrompt, global.ExpandPrompt, unread.ExpandPrompt));
        Assert.Contains("review prompt could not be read", Assert.Throws<InvalidOperationException>(() => unread.Build(now, string.Empty)).Message);
        unread.UseExpand = true;
        Assert.Contains("expand prompt could not be read", Assert.Throws<InvalidOperationException>(() => unread.Build(now, string.Empty)).Message);
    }

    [Fact]
    public void Build_TurnsTheMinutesIntoSecondsAndTheConditionIntoItsParameters()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2.5m;

        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);
        panel.Runs = 7;
        LoopSettings runs = panel.Build(now, string.Empty);
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.ForDuration);
        panel.Hours = 2;
        panel.Minutes = 30;
        LoopSettings duration = panel.Build(now, string.Empty);

        Assert.Equal((150, LoopCondition.Runs, 7), (runs.IntervalSeconds, runs.Condition, runs.Runs));
        Assert.Equal((LoopCondition.ForDuration, 9000), (duration.Condition, duration.DurationSeconds));
    }

    [Fact]
    public void Build_EndsAtTheNextTimeItIsTheChosenTimeOfDay()
    {
        LoopViewModel panel = Opened();
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.UntilTime);
        DateTimeOffset local = new(new DateTime(2026, 11, 3, 15, 0, 0));

        panel.EndTime = new TimeSpan(17, 30, 0);
        DateTimeOffset later = panel.Build(local, string.Empty).Until!.Value;
        panel.EndTime = new TimeSpan(9, 0, 0);
        DateTimeOffset tomorrow = panel.Build(local, string.Empty).Until!.Value;

        Assert.Equal(new DateTime(2026, 11, 3, 17, 30, 0), later.LocalDateTime);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0), tomorrow.LocalDateTime);
    }

    [Theory]
    [InlineData(LoopCondition.Forever, "", null, "Write the prompt")]
    [InlineData(LoopCondition.Runs, "go", 0, "at least one run")]
    [InlineData(LoopCondition.ForDuration, "go", 0, "between a minute and 24 days")]
    public void Build_RefusesWhatIsMissing(LoopCondition condition, string prompt, int? amount, string expected)
    {
        LoopViewModel panel = Panel();
        panel.UseCustom = true;
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == condition);
        panel.Runs = amount;
        panel.Hours = 0;
        panel.Minutes = 0;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => panel.Build(now, prompt));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Build_TakesAnEmptyIntervalAsZero()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = null;

        Assert.Equal(0, panel.Build(now, string.Empty).IntervalSeconds);
        Assert.Null(panel.IntervalMinutes);
    }

    [Fact]
    public void Build_RefusesAnIntervalOutOfRange()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2000;

        Assert.Contains("between 0 and 1440", Assert.Throws<InvalidOperationException>(() => panel.Build(now, string.Empty)).Message);
    }

    [Fact]
    public async Task Prepare_OffersTheUsageWindowConditionOnlyWhenTheServiceReportsWindows()
    {
        Windows();
        LoopViewModel without = Panel();
        await without.Prepare(false);
        Windows("Session", "Week");
        LoopViewModel with = Panel();
        await with.Prepare(false);

        Assert.DoesNotContain(without.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Contains(with.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Equal(["Session", "Week"], with.Windows);
        Assert.Equal("Session", with.SelectedWindow);
    }

    [Fact]
    public async Task Prepare_StartsFromTheReviewPromptEveryFifteenMinutesUntilCancelledEvenAfterEarlierChanges()
    {
        Windows();
        LoopViewModel fresh = Opened();
        LoopSettings initial = fresh.Build(now, string.Empty);
        LoopViewModel changed = Panel();
        changed.UseCustom = true;
        changed.IntervalMinutes = 1;
        changed.SelectedCondition = changed.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);

        await changed.Prepare(false);
        LoopSettings reset = changed.Build(now, string.Empty);

        Assert.Equal(("review it", 900, LoopCondition.Forever), (initial.Prompt, initial.IntervalSeconds, initial.Condition));
        Assert.Equal(initial, reset);
        Assert.True(changed.UseReview);
    }

    [Fact]
    public async Task CommitEachRun_IsOffWheneverTheSettingsArePreparedAndGoesIntoTheSettingsOfTheLoop()
    {
        Windows();
        LoopViewModel panel = Opened();
        LoopSettings defaults = panel.Build(now, string.Empty);
        panel.CommitEachRun = true;
        LoopSettings committing = panel.Build(now, string.Empty);

        await panel.Prepare(false);

        Assert.Equal((false, true, false), (defaults.AutoCommit, committing.AutoCommit, panel.CommitEachRun));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_ChoosesWhatIsTypedWhenSomethingIs(bool typed)
    {
        Windows();
        LoopViewModel panel = Panel();

        await panel.Prepare(typed);

        Assert.Equal((typed, !typed), (panel.UseCustom, panel.UseReview));
    }
}
