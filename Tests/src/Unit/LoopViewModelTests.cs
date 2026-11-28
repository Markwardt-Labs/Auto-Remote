namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class LoopViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly DateTimeOffset now = new(2026, 11, 3, 15, 0, 0, TimeSpan.Zero);

    private LoopViewModel Opened()
    {
        Windows();
        LoopViewModel panel = Panel();
        panel.Prepare().GetAwaiter().GetResult();
        return panel;
    }

    private LoopViewModel Panel() => new(connection.Object, workspace);

    private void Windows(params string[] names) => connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [.. names.Select(name => new UsagePeriod { Name = name, PercentUsed = 10, WindowMinutes = 300 })] });

    [Fact]
    public void Build_SendsThePromptItIsGivenUncommittedAndRunsForeverBackToBackWhenAsked()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 0;

        LoopSettings built = panel.Build(now, "check the logs");

        Assert.Equal(("check the logs", 0, LoopCondition.Forever, false), (built.Prompt, built.IntervalSeconds, built.Condition, built.AutoCommit));
    }

    [Fact]
    public void Build_TurnsTheMinutesIntoSecondsAndTheConditionIntoItsParameters()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2.5m;

        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);
        panel.Runs = 7;
        LoopSettings runs = panel.Build(now, "go");
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.ForDuration);
        panel.Hours = 2;
        panel.Minutes = 30;
        LoopSettings duration = panel.Build(now, "go");

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
        DateTimeOffset later = panel.Build(local, "go").Until!.Value;
        panel.EndTime = new TimeSpan(9, 0, 0);
        DateTimeOffset tomorrow = panel.Build(local, "go").Until!.Value;

        Assert.Equal(new DateTime(2026, 11, 3, 17, 30, 0), later.LocalDateTime);
        Assert.Equal(new DateTime(2026, 11, 4, 9, 0, 0), tomorrow.LocalDateTime);
    }

    [Theory]
    [InlineData(LoopCondition.Forever, "", null, "nothing for the loop to send")]
    [InlineData(LoopCondition.Runs, "go", 0, "at least one run")]
    [InlineData(LoopCondition.ForDuration, "go", 0, "between a minute and 24 days")]
    public void Build_RefusesWhatIsMissing(LoopCondition condition, string prompt, int? amount, string expected)
    {
        LoopViewModel panel = Panel();
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

        Assert.Equal(0, panel.Build(now, "go").IntervalSeconds);
        Assert.Null(panel.IntervalMinutes);
    }

    [Fact]
    public void Build_RefusesAnIntervalOutOfRange()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2000;

        Assert.Contains("between 0 and 1440", Assert.Throws<InvalidOperationException>(() => panel.Build(now, "go")).Message);
    }

    [Fact]
    public async Task Prepare_OffersTheUsageWindowConditionOnlyWhenTheServiceReportsWindows()
    {
        Windows();
        LoopViewModel without = Panel();
        await without.Prepare();
        Windows("Session", "Week");
        LoopViewModel with = Panel();
        await with.Prepare();

        Assert.DoesNotContain(without.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Contains(with.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Equal(["Session", "Week"], with.Windows);
        Assert.Equal("Session", with.SelectedWindow);
    }

    [Fact]
    public async Task Prepare_StartsEveryFifteenMinutesUntilCancelledEvenAfterEarlierChanges()
    {
        Windows();
        LoopViewModel fresh = Opened();
        LoopSettings initial = fresh.Build(now, "go");
        LoopViewModel changed = Panel();
        changed.IntervalMinutes = 1;
        changed.SelectedCondition = changed.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);

        await changed.Prepare();
        LoopSettings reset = changed.Build(now, "go");

        Assert.Equal((900, LoopCondition.Forever), (initial.IntervalSeconds, initial.Condition));
        Assert.Equal(initial, reset);
    }
}
