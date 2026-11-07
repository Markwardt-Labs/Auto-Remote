namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class UsageServiceTests
{
    private readonly FakePrompter prompter = new();

    private readonly UsageService service;

    public UsageServiceTests()
    {
        Mock<IPrompterHost> host = new();
        host.Setup(x => x.Prompter).Returns(prompter);
        host.Setup(x => x.Setup(It.IsAny<AiService>(), It.IsAny<string?>())).Returns((AiService ai, string? key) => ai switch { AiService.Codex => new CodexSetup(), AiService.Local => new LocalSetup(), _ => new ClaudeSetup() });
        service = new UsageService(host.Object);
    }

    private PromptWindow Window(string name, double used, TimeSpan length, DateTimeOffset? resets) => new() { Name = name, Used = used, Length = length, ReadAt = DateTimeOffset.UtcNow, ResetsAt = resets };

    private void Report(params PromptWindow[] windows) => prompter.TestResult = new PromptTestResult { IsAvailable = true, Windows = windows };

    [Fact]
    public async Task Get_ListsEveryWindowTheProviderReportsInItsOrderWithItsName()
    {
        DateTimeOffset session = DateTimeOffset.UtcNow.AddHours(2);
        DateTimeOffset week = DateTimeOffset.UtcNow.AddDays(3);
        Report(Window("Session", 0.4246, TimeSpan.FromHours(5), session), Window("Week", 0.1, TimeSpan.FromDays(7), week), Window("Week (Opus)", 0.9, TimeSpan.FromDays(7), week), Window("Monthly credits", 0.5, TimeSpan.FromDays(30), null));

        UsageStatus status = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.IsType<ClaudeSetup>(prompter.Tests.Single());
        Assert.Equal(AiService.ClaudeCode, status.Service);
        Assert.Equal(["Session", "Week", "Week (Opus)", "Monthly credits"], status.Windows.Select(window => window.Name).ToList());
        Assert.Equal((42.5, 300, session), (status.Windows[0].PercentUsed, status.Windows[0].WindowMinutes, status.Windows[0].ResetsAt));
        Assert.Equal((10, 10080, week), (status.Windows[1].PercentUsed, status.Windows[1].WindowMinutes, status.Windows[1].ResetsAt));
        Assert.Equal((50, 43200, null), (status.Windows[3].PercentUsed, status.Windows[3].WindowMinutes, status.Windows[3].ResetsAt));
    }

    [Fact]
    public async Task Get_TestsTheSetupOfTheService()
    {
        Report(Window("5 hours", 0.05, TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddHours(1)));

        UsageStatus status = await service.Get(AiService.Codex, CancellationToken.None);

        Assert.IsType<CodexSetup>(prompter.Tests.Single());
        Assert.Equal("5 hours", status.Windows.Single().Name);
    }

    [Fact]
    public async Task Get_ShowsAWindowWhoseResetPassedAsUnused()
    {
        Report(Window("Session", 0.9, TimeSpan.FromHours(5), DateTimeOffset.UtcNow.AddMinutes(-5)));

        UsageStatus status = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Equal(0, status.Windows.Single().PercentUsed);
        Assert.Null(status.Windows.Single().ResetsAt);
    }

    [Fact]
    public async Task Get_ReadsOncePerMinute()
    {
        Report(Window("Session", 0.01, TimeSpan.FromHours(5), null));

        await service.Get(AiService.ClaudeCode, CancellationToken.None);
        await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Single(prompter.Tests);
    }

    [Fact]
    public async Task Get_AnswersWithoutWindowsWhenTheServiceCannotBeRead()
    {
        prompter.FailTests("no subscription");

        UsageStatus status = await service.Get(AiService.Codex, CancellationToken.None);

        Assert.Equal(AiService.Codex, status.Service);
        Assert.Empty(status.Windows);
    }

    [Fact]
    public async Task Get_KeepsTheLastReadingWhenALaterReadFails()
    {
        Report(Window("Session", 0.3, TimeSpan.FromHours(5), null));
        service.Note(AiService.Codex, [Window("5 hours", 0.2, TimeSpan.FromHours(5), null)]);

        UsageStatus status = await service.Get(AiService.Codex, CancellationToken.None);

        Assert.Equal("5 hours", status.Windows.Single().Name);
        Assert.Empty(prompter.Tests);
    }

    [Fact]
    public async Task Note_ReplacesTheCachedReadingWithWhatARunningSessionReported()
    {
        Report(Window("Session", 0.3, TimeSpan.FromHours(5), null));
        await service.Get(AiService.ClaudeCode, CancellationToken.None);

        service.Note(AiService.ClaudeCode, [Window("Session", 0.6, TimeSpan.FromHours(5), null)]);
        UsageStatus status = await service.Get(AiService.ClaudeCode, CancellationToken.None);

        Assert.Equal(60, status.Windows.Single().PercentUsed);
        Assert.Single(prompter.Tests);
    }
}
