namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class UsageViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private UsageStatus Status(double session, int sessionMinutesLeft, double week) => new()
    {
        Service = AiService.ClaudeCode,
        Session = new UsagePeriod { PercentUsed = session, WindowMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddMinutes(sessionMinutesLeft) },
        Week = new UsagePeriod { PercentUsed = week, WindowMinutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(3) }
    };

    [Fact]
    public async Task Show_ReadsTheLimitsOfTheServiceAndColorsTheSessionByPace()
    {
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(80, 150, 10));
        using UsageViewModel model = new(connection.Object);

        await model.Show(AiService.ClaudeCode);

        Assert.True(model.HasLimits);
        Assert.Equal("Claude Code", model.ServiceName);
        Assert.Equal("80% used", model.SessionText);
        Assert.Equal("VeryHigh", model.SessionPace);
        Assert.Equal("VeryLow", model.WeekPace);
        Assert.StartsWith("Resets in 2h", model.SessionReset);
        Assert.StartsWith("Resets in 2d", model.WeekReset);
    }

    [Fact]
    public async Task Show_SwitchingServiceReadsTheNewServiceAndForgetsTheOld()
    {
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(50, 150, 10));
        connection.Setup(x => x.GetUsage(AiService.Codex, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus { Service = AiService.Codex });
        using UsageViewModel model = new(connection.Object);
        await model.Show(AiService.ClaudeCode);

        await model.Show(AiService.Codex);

        Assert.Equal("OpenAI Codex", model.ServiceName);
        Assert.False(model.HasLimits);
        Assert.Equal("Even", model.SessionPace);
        Assert.Equal(string.Empty, model.SessionText);
    }

    [Fact]
    public async Task Show_KeepsTheLastLimitsWhenARefreshIsRefused()
    {
        connection.SetupSequence(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(50, 150, 10)).ThrowsAsync(new RemoteException("busy"));
        using UsageViewModel model = new(connection.Object);
        await model.Show(AiService.ClaudeCode);

        await model.Show(AiService.ClaudeCode);

        Assert.Equal("50% used", model.SessionText);
    }

    [Fact]
    public async Task Show_NothingShowsNoLimitsAndAsksNothing()
    {
        using UsageViewModel model = new(connection.Object);

        await model.Show(null);

        Assert.False(model.HasLimits);
        connection.Verify(x => x.GetUsage(It.IsAny<AiService>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
