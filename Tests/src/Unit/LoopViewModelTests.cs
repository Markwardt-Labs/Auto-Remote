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
        panel.Open().GetAwaiter().GetResult();
        return panel;
    }

    private LoopViewModel Panel() => new(connection.Object, workspace);

    private void Windows(params string[] names) => connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.ClaudeCode, Windows = [.. names.Select(name => new UsagePeriod { Name = name, PercentUsed = 10, WindowMinutes = 300 })] });

    [Fact]
    public async Task Build_SendsTheReviewPromptOrTheUsersOwnAndRunsForeverBackToBackWhenAsked()
    {
        Windows();
        LoopViewModel panel = Panel();
        await panel.Open();
        panel.IntervalMinutes = 0;
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.Forever);

        LoopSettings review = panel.Build(now);
        panel.UseCustom = true;
        panel.CustomPrompt = "  check the logs  ";
        LoopSettings custom = panel.Build(now);

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
        LoopSettings expand = panel.Build(now);
        bool afterExpand = panel.UseReview || panel.UseCustom;
        panel.UseExpand = false;
        bool ignoredFalse = panel.UseExpand;
        panel.UseCustom = true;
        panel.UseReview = true;
        panel.UseCustom = false;
        LoopSettings review = panel.Build(now);
        await panel.Open();

        Assert.Equal(("expand it", false, true), (expand.Prompt, afterExpand, ignoredFalse));
        Assert.Equal("review it", review.Prompt);
        Assert.Contains(nameof(LoopViewModel.UseExpand), changed);
        Assert.True(panel.UseReview);
    }

    [Fact]
    public async Task Open_ReadsTheReviewPromptOfTheWorkspaceOrTheGlobalOneAndBuildRefusesWhenItCouldNotBeRead()
    {
        Windows();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { ReviewPrompt = "  custom review ", ExpandPrompt = " custom expand ", GlobalReviewPrompt = "global review", GlobalExpandPrompt = "global expand" });
        LoopViewModel own = Panel();
        await own.Open();

        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "global review", GlobalExpandPrompt = "global expand" });
        LoopViewModel global = Panel();
        await global.Open();

        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        LoopViewModel unread = Panel();
        await unread.Open();

        Assert.Equal(("custom review", "global review", string.Empty), (own.ReviewPrompt, global.ReviewPrompt, unread.ReviewPrompt));
        Assert.Equal(("custom expand", "global expand", string.Empty), (own.ExpandPrompt, global.ExpandPrompt, unread.ExpandPrompt));
        Assert.Contains("review prompt could not be read", Assert.Throws<InvalidOperationException>(() => unread.Build(now)).Message);
        unread.UseExpand = true;
        Assert.Contains("expand prompt could not be read", Assert.Throws<InvalidOperationException>(() => unread.Build(now)).Message);
    }

    [Fact]
    public void Build_TurnsTheMinutesIntoSecondsAndTheConditionIntoItsParameters()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2.5m;

        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);
        panel.Runs = 7;
        LoopSettings runs = panel.Build(now);
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.ForDuration);
        panel.Hours = 2;
        panel.Minutes = 30;
        LoopSettings duration = panel.Build(now);

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
        DateTimeOffset later = panel.Build(local).Until!.Value;
        panel.EndTime = new TimeSpan(9, 0, 0);
        DateTimeOffset tomorrow = panel.Build(local).Until!.Value;

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
        panel.CustomPrompt = prompt;
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == condition);
        panel.Runs = amount;
        panel.Hours = 0;
        panel.Minutes = 0;

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => panel.Build(now));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public void Build_RefusesAnIntervalOutOfRange()
    {
        LoopViewModel panel = Opened();
        panel.IntervalMinutes = 2000;

        Assert.Contains("between 0 and 1440", Assert.Throws<InvalidOperationException>(() => panel.Build(now)).Message);
    }

    [Fact]
    public async Task Open_OffersTheUsageWindowConditionOnlyWhenTheServiceReportsWindows()
    {
        Windows();
        LoopViewModel without = Panel();
        await without.Open();
        Windows("Session", "Week");
        LoopViewModel with = Panel();
        await with.Open();

        Assert.DoesNotContain(without.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Contains(with.Conditions, choice => choice.Condition == LoopCondition.UntilWindow);
        Assert.Equal(["Session", "Week"], with.Windows);
        Assert.Equal("Session", with.SelectedWindow);
        Assert.True(with.IsOpen);
    }

    [Fact]
    public async Task Open_StartsFromTheReviewPromptEveryFifteenMinutesUntilCancelledEvenAfterEarlierChanges()
    {
        Windows();
        LoopViewModel fresh = Opened();
        LoopSettings initial = fresh.Build(now);
        LoopViewModel changed = Panel();
        changed.UseCustom = true;
        changed.CustomPrompt = "other";
        changed.IntervalMinutes = 1;
        changed.SelectedCondition = changed.Conditions.Single(choice => choice.Condition == LoopCondition.Runs);

        await changed.Open();
        LoopSettings reset = changed.Build(now);

        Assert.Equal(("review it", 900, LoopCondition.Forever), (initial.Prompt, initial.IntervalSeconds, initial.Condition));
        Assert.Equal(initial, reset);
        Assert.True(changed.UseReview);
    }

    [Fact]
    public async Task Start_SendsTheSettingsToTheServerAndClosesThePanel()
    {
        Windows("Session", "Week");
        LoopViewModel panel = Panel();
        await panel.Open();
        panel.SelectedCondition = panel.Conditions.Single(choice => choice.Condition == LoopCondition.UntilWindow);
        panel.SelectedWindow = "Week";
        panel.Percent = 80;
        LoopSettings? sent = null;
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).Callback<Guid, LoopSettings, CancellationToken>((_, settings, _) => sent = settings).Returns(Task.CompletedTask);
        bool started = false;
        panel.Started += () => started = true;

        panel.StartCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && !started; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(started);
        Assert.False(panel.IsOpen);
        Assert.Equal((LoopCondition.UntilWindow, "Week", 80d, "review it"), (sent!.Condition, sent.Window, sent.Percent, sent.Prompt));
    }

    [Fact]
    public async Task Start_KeepsThePanelOpenAndShowsWhyWhenTheServerRefusesOrTheInputIsNotEnough()
    {
        Windows();
        LoopViewModel panel = Panel();
        await panel.Open();
        connection.Setup(x => x.StartLoop(workspace.Id, It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("A loop already runs in this workspace."));

        panel.StartCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && !panel.HasError; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(panel.IsOpen);
        Assert.Equal("A loop already runs in this workspace.", panel.Error);

        panel.UseCustom = true;
        panel.CustomPrompt = " ";
        panel.StartCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && panel.Error != "Write the prompt the loop sends."; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("Write the prompt the loop sends.", panel.Error);
    }

    [Fact]
    public void Cancel_ClosesThePanelWithoutStartingAnything()
    {
        LoopViewModel panel = Panel();
        panel.IsOpen = true;

        panel.CancelCommand.Execute(null);

        Assert.False(panel.IsOpen);
        connection.Verify(x => x.StartLoop(It.IsAny<Guid>(), It.IsAny<LoopSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
