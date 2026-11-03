namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class UsageViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private UsageStatus Status(double session, int sessionMinutesLeft, double week) => new()
    {
        Service = AiService.ClaudeCode,
        Windows =
        [
            new UsagePeriod { Name = "Session", PercentUsed = session, WindowMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddMinutes(sessionMinutesLeft) },
            new UsagePeriod { Name = "Week", PercentUsed = week, WindowMinutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(3) }
        ]
    };

    [Fact]
    public async Task Show_ListsEveryWindowTheServiceReportsAndColorsEachByPace()
    {
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(80, 150, 10) with { Windows = [.. Status(80, 150, 10).Windows, new UsagePeriod { Name = "Week (Opus)", PercentUsed = 0, WindowMinutes = 10080 }] });
        using UsageViewModel model = new(connection.Object);

        await model.Show(AiService.ClaudeCode);

        Assert.True(model.HasLimits);
        Assert.Equal("Claude Code", model.ServiceName);
        Assert.Equal(["Session", "Week", "Week (Opus)"], model.Windows.Select(line => line.Name).ToList());
        Assert.Equal(["80% used", "10% used", "0% used"], model.Windows.Select(line => line.Text).ToList());
        Assert.Equal(["VeryHigh", "VeryLow", "Even"], model.Windows.Select(line => line.Pace).ToList());
        Assert.StartsWith("Resets in 2h", model.Windows[0].Reset);
        Assert.StartsWith("Resets in 2d", model.Windows[1].Reset);
        Assert.Equal(string.Empty, model.Windows[2].Reset);
    }

    [Fact]
    public async Task Show_ColorsTheIconByTheShortestWindow()
    {
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(new UsageStatus
        {
            Service = AiService.ClaudeCode,
            Windows =
            [
                new UsagePeriod { Name = "Week", PercentUsed = 100, WindowMinutes = 10080, ResetsAt = DateTimeOffset.UtcNow.AddDays(3) },
                new UsagePeriod { Name = "Session", PercentUsed = 80, WindowMinutes = 300, ResetsAt = DateTimeOffset.UtcNow.AddMinutes(150) }
            ]
        });
        using UsageViewModel model = new(connection.Object);

        await model.Show(AiService.ClaudeCode);

        Assert.Equal("VeryHigh", model.IconPace);
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(50, 150, 100));
        await model.Show(AiService.ClaudeCode);
        Assert.Equal("Even", model.IconPace);
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
        Assert.Equal("Even", model.IconPace);
        Assert.Empty(model.Windows);
    }

    [Fact]
    public async Task Show_KeepsTheLastLimitsWhenARefreshIsRefused()
    {
        connection.SetupSequence(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(50, 150, 10)).ThrowsAsync(new RemoteException("busy"));
        using UsageViewModel model = new(connection.Object);
        await model.Show(AiService.ClaudeCode);

        await model.Show(AiService.ClaudeCode);

        Assert.Equal("50% used", model.Windows[0].Text);
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
