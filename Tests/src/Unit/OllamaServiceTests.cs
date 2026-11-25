namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class OllamaServiceTests
{
    private readonly List<(string Command, Dictionary<string, string> Variables)> launched = [];

    private readonly List<Fake> runs = [];

    private bool up;

    private bool launching = true;

    private bool answers = true;

    private bool outsideRunning;

    private sealed class Fake(Action stopped) : IOllamaRun
    {
        public bool IsRunning { get; set; } = true;

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            IsRunning = false;
            stopped();
        }
    }

    private OllamaService Service() => new((_, _) => Task.FromResult(up || outsideRunning), (command, variables) =>
    {
        launched.Add((command, new Dictionary<string, string>(variables)));

        if (!launching)
        {
            return null;
        }

        Fake run = new(() => up = false);
        runs.Add(run);
        up = answers;
        return run;
    }, Mock.Of<IOllamaServiceUnit>())
    {
        StartWait = TimeSpan.FromMilliseconds(100),
        StopWait = TimeSpan.FromMilliseconds(100),
        Poll = TimeSpan.FromMilliseconds(5)
    };

    private AiEnvironment Environment(bool optimize = false, string server = "http://localhost:11434/v1") => new() { LocalServer = server, LocalOptimizeContext = optimize, OllamaCommand = "/opt/ollama" };

    [Fact]
    public async Task Ensure_StartsOllamaThatIsNotRunningWithNoLoadTimeoutAndNoOptimizationUnlessAsked()
    {
        OllamaService service = Service();

        await service.Ensure(Environment());

        (string command, Dictionary<string, string> variables) = Assert.Single(launched);
        Assert.Equal("/opt/ollama", command);
        Assert.Equal(new Dictionary<string, string> { ["OLLAMA_HOST"] = "localhost:11434", ["OLLAMA_LOAD_TIMEOUT"] = "0" }, variables);
        Assert.Contains("no limit on how long a model load may stall", service.Note);
        Assert.DoesNotContain("q4_0", service.Note);
    }

    [Fact]
    public async Task Ensure_StartsOllamaWithAQuantizedCacheAndFlashAttentionWhenTheContextIsOptimized()
    {
        OllamaService service = Service();

        await service.Ensure(Environment(true, "http://127.0.0.1:11500/v1"));

        Dictionary<string, string> variables = Assert.Single(launched).Variables;
        Assert.Equal(new Dictionary<string, string> { ["OLLAMA_HOST"] = "127.0.0.1:11500", ["OLLAMA_LOAD_TIMEOUT"] = "0", ["OLLAMA_KV_CACHE_TYPE"] = "q4_0", ["OLLAMA_FLASH_ATTENTION"] = "1" }, variables);
        Assert.Contains("q4_0", service.Note);
    }

    [Fact]
    public async Task Ensure_StartsOllamaOnlyOnceWhileItRunsAndRestartsTheOneItStartedWhenTheChoiceChanges()
    {
        OllamaService service = Service();

        await service.Ensure(Environment());
        await service.Ensure(Environment());
        Assert.Single(launched);

        await service.Ensure(Environment(true));

        Assert.Equal(2, launched.Count);
        Assert.True(runs[0].Disposed);
        Assert.Equal("1", launched[1].Variables["OLLAMA_FLASH_ATTENTION"]);
        Assert.Contains("q4_0", service.Note);

        await service.Ensure(Environment(false));

        Assert.Equal(3, launched.Count);
        Assert.False(launched[2].Variables.ContainsKey("OLLAMA_KV_CACHE_TYPE"));
    }

    [Fact]
    public async Task Ensure_StartsItAgainWhenTheOneItStartedStoppedOnItsOwn()
    {
        OllamaService service = Service();
        await service.Ensure(Environment());
        runs[0].IsRunning = false;
        up = false;

        await service.Ensure(Environment());

        Assert.Equal(2, launched.Count);
    }

    [Theory]
    [InlineData(false, "OLLAMA_LOAD_TIMEOUT=0")]
    [InlineData(true, "OLLAMA_KV_CACHE_TYPE=q4_0, OLLAMA_FLASH_ATTENTION=1 and OLLAMA_LOAD_TIMEOUT=0")]
    public async Task Ensure_LeavesAnOllamaThatRunsOutsideAutoRemoteAloneAndSaysWhichVariablesItNeeds(bool optimize, string variables)
    {
        outsideRunning = true;
        OllamaService service = Service();

        await service.Ensure(Environment(optimize));

        Assert.Empty(launched);
        Assert.Contains("runs outside Auto Remote", service.Note);
        Assert.Contains(variables, service.Note);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Ensure_DoesNothingForAModelServerThatIsNotOnThisMachineAndOnlySaysSoWhenAskedToOptimize(bool optimize, bool noted)
    {
        OllamaService service = Service();

        await service.Ensure(Environment(optimize, "http://gpu-box:11434/v1"));

        Assert.Empty(launched);
        Assert.Equal(noted, service.Note is not null);
    }

    [Fact]
    public async Task Ensure_DoesNothingForAnAddressThatIsNotAServer()
    {
        OllamaService service = Service();

        await service.Ensure(Environment(true, "not a url"));

        Assert.Empty(launched);
        Assert.Null(service.Note);
    }

    [Fact]
    public async Task Ensure_ReportsAnOllamaThatCannotBeStartedOrDoesNotAnswerAsANoteAndNeverThrows()
    {
        launching = false;
        OllamaService missing = Service();
        await missing.Ensure(Environment());
        Assert.Contains("could not be started with the command '/opt/ollama'", missing.Note);

        launching = true;
        answers = false;
        OllamaService silent = Service();
        await silent.Ensure(Environment());

        Assert.Contains("did not answer", silent.Note);
        Assert.True(runs[0].Disposed);
    }

    [Fact]
    public async Task Ensure_UsesTheOllamaCommandByDefaultWhenTheEnvironmentHasNone()
    {
        OllamaService service = Service();

        await service.Ensure(Environment() with { OllamaCommand = " " });

        Assert.Equal("ollama", launched[0].Command);
    }

    [Fact]
    public async Task DisposeAsync_StopsTheOllamaItStarted()
    {
        OllamaService service = Service();
        await service.Ensure(Environment());

        await service.DisposeAsync();

        Assert.True(runs[0].Disposed);
    }

    private readonly Mock<IOllamaServiceUnit> unit = new();

    private OllamaService Controlled(bool available = true, bool needsPassword = true)
    {
        unit.Setup(x => x.IsAvailable(It.IsAny<CancellationToken>())).ReturnsAsync(available);
        unit.Setup(x => x.NeedsPassword(It.IsAny<CancellationToken>())).ReturnsAsync(needsPassword);
        return new OllamaService((_, _) => Task.FromResult(up), (_, _) => null, unit.Object)
        {
            StartWait = TimeSpan.FromMilliseconds(100),
            StopWait = TimeSpan.FromMilliseconds(100),
            ServiceWait = TimeSpan.FromMilliseconds(100),
            Poll = TimeSpan.FromMilliseconds(5)
        };
    }

    [Fact]
    public async Task Refresh_FindsOutWhetherTheServiceCanBeControlledAndNeedsAPassword()
    {
        OllamaService service = Controlled(true, true);

        await service.Refresh(Environment());

        Assert.Equal((true, true), (service.ServiceControl, service.ServiceNeedsPassword));
    }

    [Fact]
    public async Task Refresh_OffersNoControlWithoutASystemdServiceOrForAModelServerOnAnotherMachine()
    {
        OllamaService without = Controlled(false);
        await without.Refresh(Environment());
        OllamaService remote = Controlled(true);
        await remote.Refresh(Environment(server: "http://box:11434/v1"));

        Assert.Equal((false, false), (without.ServiceControl, without.ServiceNeedsPassword));
        Assert.Equal((false, false), (remote.ServiceControl, remote.ServiceNeedsPassword));
    }

    [Fact]
    public async Task ConfigureService_AppliesTheChangeWithThePasswordWhenOneIsNeededAndWaitsForTheServiceToAnswer()
    {
        OllamaService service = Controlled(true, true);
        up = true;
        unit.Setup(x => x.Matches(true)).Returns(false);
        unit.Setup(x => x.Apply(true, "pw", It.IsAny<CancellationToken>())).ReturnsAsync(new RootResult(RootOutcome.Done));

        RootResult result = await service.ConfigureService(Environment(true), true, "pw");

        Assert.Equal(RootOutcome.Done, result.Outcome);
        Assert.Contains("configured the Ollama service", service.Note);
        unit.Verify(x => x.Apply(true, "pw", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureService_NeverPassesAPasswordWhenSudoNeedsNone()
    {
        OllamaService service = Controlled(true, false);
        up = true;
        unit.Setup(x => x.Matches(false)).Returns(false);
        unit.Setup(x => x.Apply(false, null, It.IsAny<CancellationToken>())).ReturnsAsync(new RootResult(RootOutcome.Done));

        RootResult result = await service.ConfigureService(Environment(), false, "ignored");

        Assert.Equal(RootOutcome.Done, result.Outcome);
        Assert.Null(service.Note);
    }

    [Fact]
    public async Task ConfigureService_DoesNotRestartAServiceThatAlreadyHasTheSetting()
    {
        OllamaService service = Controlled();
        up = true;
        unit.Setup(x => x.Matches(true)).Returns(true);

        RootResult result = await service.ConfigureService(Environment(true), true, null);

        Assert.Equal(RootOutcome.Done, result.Outcome);
        unit.Verify(x => x.Apply(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfigureService_PassesOnAWrongPasswordAndLeavesTheNoteAlone()
    {
        OllamaService service = Controlled();
        up = true;
        unit.Setup(x => x.Matches(true)).Returns(false);
        unit.Setup(x => x.Apply(true, "bad", It.IsAny<CancellationToken>())).ReturnsAsync(new RootResult(RootOutcome.WrongPassword));

        RootResult result = await service.ConfigureService(Environment(true), true, "bad");

        Assert.Equal(RootOutcome.WrongPassword, result.Outcome);
        Assert.Null(service.Note);
    }

    [Fact]
    public async Task ConfigureService_FailsWhenTheRestartedServiceNeverAnswers()
    {
        OllamaService service = Controlled();
        up = false;
        unit.Setup(x => x.Matches(true)).Returns(false);
        unit.Setup(x => x.Apply(true, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new RootResult(RootOutcome.Done));

        RootResult result = await service.ConfigureService(Environment(true), true, "pw");

        Assert.Equal((RootOutcome.Failed, "The Ollama service was restarted but did not answer."), (result.Outcome, result.Message));
    }

    [Fact]
    public async Task ConfigureService_RefusesWhenThereIsNoServiceToConfigure()
    {
        OllamaService service = Controlled(false);

        RootResult result = await service.ConfigureService(Environment(true), true, "pw");

        Assert.Equal(RootOutcome.Failed, result.Outcome);
        unit.Verify(x => x.Apply(It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConfigureService_StopsTheOllamaAutoRemoteStartedItselfBeforeTakingTheServiceOver()
    {
        OllamaService service = Service();
        await service.Ensure(Environment());
        Assert.Single(runs);
        OllamaService controlled = new((_, _) => Task.FromResult(up), (_, _) => null, unit.Object) { Poll = TimeSpan.FromMilliseconds(5), ServiceWait = TimeSpan.FromMilliseconds(100) };
        unit.Setup(x => x.IsAvailable(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        unit.Setup(x => x.NeedsPassword(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        unit.Setup(x => x.Matches(true)).Returns(true);

        RootResult result = await controlled.ConfigureService(Environment(true), true, null);

        Assert.Equal(RootOutcome.Done, result.Outcome);
    }

    [Theory]
    [InlineData(true, true, "configured the Ollama service")]
    [InlineData(true, false, "has not configured")]
    [InlineData(false, false, "turn Optimize context on")]
    public async Task Ensure_TellsAnOllamaServiceThatRunsOutsideAutoRemoteWhatToDo(bool wanted, bool matches, string expected)
    {
        OllamaService service = Controlled();
        up = true;
        unit.Setup(x => x.Matches(true)).Returns(matches);

        await service.Ensure(Environment(wanted));

        Assert.Contains(expected, service.Note);
        Assert.Empty(launched);
    }
}
