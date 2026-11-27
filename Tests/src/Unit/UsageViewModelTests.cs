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

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidDataException))]
    [InlineData(typeof(RemoteException))]
    [InlineData(typeof(InvalidOperationException))]
    public async Task Show_KeepsReadingAfterAReadFailsWithAnything(Type kind)
    {
        Exception failure = kind == typeof(SocketException) ? new SocketException() : (Exception)Activator.CreateInstance(kind, "the connection was lost")!;
        int reads = 0;
        TaskCompletionSource failed = new();
        double session = 12;
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).Returns(() =>
        {
            // The read after the first one fails, as one does that runs while the server is away.
            if (Interlocked.Increment(ref reads) is 2 or 3)
            {
                failed.TrySetResult();
                return Task.FromException<UsageStatus>(failure);
            }

            return Task.FromResult(Status(session, 150, 1));
        });
        using UsageViewModel model = new(connection.Object, TimeSpan.FromMilliseconds(10));
        await model.Show(AiService.ClaudeCode);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("12% used", model.Windows[0].Text);

        session = 45;

        await Until(() => model.Windows[0].Text == "45% used");
    }

    [Fact]
    public async Task Show_ReadsAgainAsSoonAsTheConnectionIsBack()
    {
        connection.SetupSequence(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(Status(12, 150, 1)).ReturnsAsync(Status(45, 150, 17));
        using UsageViewModel model = new(connection.Object, TimeSpan.FromHours(1));
        await model.Show(AiService.ClaudeCode);

        connection.Raise(x => x.Reconnected += null);

        Assert.Equal(["45% used", "17% used"], model.Windows.Select(line => line.Text).ToList());
    }

    [Fact]
    public async Task Dispose_EndsTheReadingAlsoWhenTheConnectionComesBack()
    {
        int reads = 0;
        connection.Setup(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Interlocked.Increment(ref reads);
            return Status(12, 150, 1);
        });
        UsageViewModel model = new(connection.Object, TimeSpan.FromMilliseconds(10));
        await model.Show(AiService.ClaudeCode);
        await Until(() => Volatile.Read(ref reads) > 2);

        model.Dispose();
        // A read that was starting as the view went may still be counted.
        await Task.Delay(50);
        int ended = Volatile.Read(ref reads);
        connection.Raise(x => x.Reconnected += null);
        await Task.Delay(100);

        Assert.Equal(ended, Volatile.Read(ref reads));
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
