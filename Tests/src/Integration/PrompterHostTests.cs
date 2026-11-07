namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class PrompterHostTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly FakePrompter prompter = new();

    private readonly List<string> folders = [];

    private readonly List<Uri?> localAsked = [];

    private readonly List<Uri?> imageAsked = [];

    private Exception? failure;

    private PrompterHost Host() => new(folder =>
    {
        folders.Add(folder);
        return prompter;
    }, (server, _) =>
    {
        localAsked.Add(server);
        return failure is null ? Task.FromResult<IReadOnlyList<string>>(["gemma", "qwen"]) : Task.FromException<IReadOnlyList<string>>(failure);
    }, (server, _) =>
    {
        imageAsked.Add(server);
        return Task.FromResult<IReadOnlyList<string>>(["sdxl"]);
    });

    private readonly BuiltInInstructions builtIn = new();

    private ServerConfig Config => new() { Port = 0, Key = "k", DataFolder = temp.Path };

    private ServerConfig OldConfig(string json) => JsonSerializer.Deserialize<ServerConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })! with { DataFolder = temp.Path };

    private async Task<PrompterHost> Configured()
    {
        PrompterHost host = Host();
        await host.Configure(Config, CancellationToken.None);
        return host;
    }

    private CapabilityNames Names(string name) => new() { Fast = name, Balanced = name, Strong = name, Max = name };

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task Configure_MakesThePrompterInTheLiveAiFolderOfTheDataFolderAndStartsWithDefaults()
    {
        PrompterHost host = await Configured();

        Assert.Equal([Path.Combine(temp.Path, "liveai")], folders);
        Assert.Same(prompter, host.Prompter);
        Assert.Equal(AiService.ClaudeCode, host.DefaultService);
        Assert.Equal("claude", host.Get().ClaudeCommand);
        Assert.Equal("claude", prompter.Environment.ClaudeCommand);
    }

    [Fact]
    public void Prompter_BeforeConfigureThrows() => Assert.Throws<InvalidOperationException>(() => Host().Prompter);

    [Fact]
    public async Task Set_AppliesTheEnvironmentToThePrompterAndKeepsItForTheNextStart()
    {
        PrompterHost host = await Configured();

        AiEnvironment saved = await host.Set(new AiEnvironment
        {
            DefaultService = AiService.Local,
            ClaudeCommand = " /opt/claude ",
            ClaudeConfigFolder = "/cfg/claude",
            CodexCommand = "codex",
            LocalCommand = "/opt/opencode",
            LocalServer = "http://gpu:8080/v1",
            LocalModels = Names("gemma"),
            LocalContexts = new CapabilityContexts { Fast = 4096, Max = 32768 }
        }, CancellationToken.None);

        PromptEnvironment applied = prompter.Environment;
        Assert.Equal(("/opt/claude", "/cfg/claude", "/opt/opencode"), (applied.ClaudeCommand, applied.ClaudeConfigFolder, applied.LocalCommand));
        Assert.Equal(new Uri("http://gpu:8080/v1"), applied.LocalSettings.Server);
        Assert.Equal("gemma", applied.LocalSettings.Models!.Balanced);
        Assert.Equal((4096, null, 32768), (applied.LocalSettings.Contexts!.Fast, applied.LocalSettings.Contexts.Balanced, applied.LocalSettings.Contexts.Max));
        Assert.Equal(AiService.Local, saved.DefaultService);

        PrompterHost next = await Configured();
        Assert.Equal(AiService.Local, next.DefaultService);
        Assert.Equal("/opt/claude", next.Get().ClaudeCommand);
        Assert.Equal("gemma", prompter.Environment.LocalSettings.Models!.Fast);
    }

    [Fact]
    public async Task Set_KeepsTheStatusIntervalAndRefusesOneOutOfRangeAndTheInstructionsOfNewSessionsCarryIt()
    {
        PrompterHost host = await Configured();
        Assert.Equal(5, host.Get().StatusMinutes);

        AiEnvironment saved = await host.Set(host.Get() with { StatusMinutes = 12 }, CancellationToken.None);
        PrompterHost next = await Configured();

        Assert.Equal(12, saved.StatusMinutes);
        Assert.Equal(12, next.Get().StatusMinutes);
        await Assert.ThrowsAsync<RequestException>(() => host.Set(host.Get() with { StatusMinutes = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => host.Set(host.Get() with { StatusMinutes = 1441 }, CancellationToken.None));
        Assert.Equal(12, host.Get().StatusMinutes);
        Assert.Equal(builtIn.Compose(null, 12), prompter.Environment.Instructions);
        Assert.Equal(builtIn.Compose("x", 4), host.ComposeInstructions("x", 4));
        Assert.Equal(builtIn.Compose("x", 12), host.ComposeInstructions("x", null));
    }

    [Fact]
    public async Task Set_KeepsTrimmedReviewAndExpandPromptsAndRestoresTheDefaultForBlankOnes()
    {
        PrompterHost host = await Configured();
        AiEnvironment defaults = new();
        Assert.Equal(("review workspace and fix any problems or inconsistencies", "expand upon content in workspace in areas that make sense"), (host.Get().ReviewPrompt, host.Get().ExpandPrompt));

        AiEnvironment own = await host.Set(host.Get() with { ReviewPrompt = "  review it  ", ExpandPrompt = "grow it" }, CancellationToken.None);
        PrompterHost next = await Configured();
        AiEnvironment blank = await host.Set(host.Get() with { ReviewPrompt = "   ", ExpandPrompt = string.Empty }, CancellationToken.None);

        Assert.Equal(("review it", "grow it"), (own.ReviewPrompt, own.ExpandPrompt));
        Assert.Equal(("review it", "grow it"), (next.Get().ReviewPrompt, next.Get().ExpandPrompt));
        Assert.Equal((defaults.ReviewPrompt, defaults.ExpandPrompt), (blank.ReviewPrompt, blank.ExpandPrompt));
    }

    [Fact]
    public async Task Set_NeverGivesAnApiKeyBackAndMergesKeysLeftOutOrEmptied()
    {
        PrompterHost host = await Configured();
        AiEnvironment first = await host.Set(new AiEnvironment { ClaudeApiKey = " sk-claude ", CodexApiKey = "sk-codex" }, CancellationToken.None);

        Assert.Equal((true, true, null, null), (first.HasClaudeApiKey, first.HasCodexApiKey, first.ClaudeApiKey, first.CodexApiKey));
        Assert.Equal("sk-claude", prompter.Environment.ClaudeApiKey);

        AiEnvironment second = await host.Set(host.Get() with { CodexApiKey = string.Empty, ClaudeCommand = "claude2" }, CancellationToken.None);

        Assert.Equal((true, false), (second.HasClaudeApiKey, second.HasCodexApiKey));
        Assert.Equal(("sk-claude", null), (prompter.Environment.ClaudeApiKey, prompter.Environment.CodexApiKey));
        string stored = await File.ReadAllTextAsync(Path.Combine(temp.Path, "environment.json"));
        Assert.Contains("sk-claude", stored);
        Assert.DoesNotContain("sk-codex", stored);
    }

    [Fact]
    public async Task Set_WritesTheFileForTheUserOnly()
    {
        PrompterHost host = await Configured();

        await host.Set(new AiEnvironment { ClaudeApiKey = "sk-claude" }, CancellationToken.None);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(temp.Path, "environment.json")));
        }

        Assert.False(File.Exists(Path.Combine(temp.Path, "environment.json.tmp")));
    }

    [Fact]
    public async Task Set_AppliesImageGenerationOfOpenAiAndOfTheLocalServer()
    {
        PrompterHost host = await Configured();

        await host.Set(new AiEnvironment { ImageGeneration = ImageGenerationKind.OpenAi, OpenAiApiKey = "sk-image" }, CancellationToken.None);
        Assert.Equal("sk-image", Assert.IsType<OpenAiImageSetup>(prompter.Environment.ImageGeneration).ApiKey);

        await host.Set(host.Get() with { ImageGeneration = ImageGenerationKind.Local, LocalImageServer = "http://gpu:7860", LocalImageModels = Names("sdxl") }, CancellationToken.None);
        LocalImageSetup local = Assert.IsType<LocalImageSetup>(prompter.Environment.ImageGeneration);
        Assert.Equal((new Uri("http://gpu:7860"), "sdxl"), (local.Server, local.Models.Strong));

        await host.Set(host.Get() with { ImageGeneration = ImageGenerationKind.None }, CancellationToken.None);
        Assert.Null(prompter.Environment.ImageGeneration);
    }

    [Fact]
    public async Task Set_LeavesTheLocalModelsUnsetUntilThereIsOneForEveryCapability()
    {
        PrompterHost host = await Configured();

        await host.Set(new AiEnvironment(), CancellationToken.None);

        Assert.Null(prompter.Environment.LocalSettings.Models);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("opencode")]
    public async Task Set_RefusesAnEmptyCommand(string tool)
    {
        PrompterHost host = await Configured();
        AiEnvironment environment = host.Get() with { ClaudeCommand = tool == "claude" ? " " : "claude", CodexCommand = tool == "codex" ? "" : "codex", LocalCommand = tool == "opencode" ? " " : "opencode" };

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => host.Set(environment, CancellationToken.None));

        Assert.Contains("command", exception.Message);
    }

    [Fact]
    public async Task Set_RefusesWhatWouldBreakTheAiAndChangesNothing()
    {
        PrompterHost host = await Configured();
        AiEnvironment good = host.Get();
        AiEnvironment[] bad =
        [
            good with { LocalServer = "localhost:11434" },
            good with { LocalContexts = new CapabilityContexts { Fast = 0 } },
            good with { LocalModels = new CapabilityNames { Fast = "only-one" } },
            good with { ImageGeneration = ImageGenerationKind.OpenAi },
            good with { ImageGeneration = ImageGenerationKind.Local, LocalImageModels = new CapabilityNames { Fast = "a" } },
            good with { ImageGeneration = ImageGenerationKind.Local, LocalImageServer = "ftp://x", LocalImageModels = Names("a") },
            good with { DefaultService = (AiService)99 }
        ];

        foreach (AiEnvironment environment in bad)
        {
            await Assert.ThrowsAsync<RequestException>(() => host.Set(environment, CancellationToken.None));
        }

        Assert.Equal(good, host.Get());
        Assert.False(File.Exists(Path.Combine(temp.Path, "environment.json")));
    }

    [Fact]
    public async Task Configure_StartsWithDefaultsWhenTheStoredFileIsDamaged()
    {
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "environment.json"), "{ not json");

        PrompterHost host = await Configured();

        Assert.Equal(new AiEnvironment(), host.Get());
    }

    [Fact]
    public async Task Setup_MakesTheKindOfSetupOfTheProviderAndPutsTheKeyInIt()
    {
        PrompterHost host = await Configured();

        Assert.Equal("sk-1", Assert.IsType<ClaudeSetup>(host.Setup(AiService.ClaudeCode, " sk-1 ")).ApiKey);
        Assert.Equal("sk-2", Assert.IsType<CodexSetup>(host.Setup(AiService.Codex, "sk-2")).ApiKey);
        Assert.Null(Assert.IsType<CodexSetup>(host.Setup(AiService.Codex, "  ")).ApiKey);
        Assert.IsType<LocalSetup>(host.Setup(AiService.Local));
    }

    [Fact]
    public async Task Setup_RefusesAKeyForTheLocalProvider()
    {
        PrompterHost host = await Configured();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => Task.FromResult(host.Setup(AiService.Local, "sk-1")));

        Assert.Contains("takes no API key", exception.Message);
    }

    [Fact]
    public async Task ListLocalModels_AsksTheGivenAddressOrTheEnvironmentsOwn()
    {
        PrompterHost host = await Configured();

        IReadOnlyList<string> own = await host.ListLocalModels(null, CancellationToken.None);
        IReadOnlyList<string> given = await host.ListLocalModels(" http://other:1234/v1 ", CancellationToken.None);

        Assert.Equal(["gemma", "qwen"], own);
        Assert.Equal(["gemma", "qwen"], given);
        Assert.Equal([new Uri("http://localhost:11434/v1"), new Uri("http://other:1234/v1")], localAsked);
    }

    [Fact]
    public async Task ListImageModels_AsksTheGivenAddressOrTheEnvironmentsOwn()
    {
        PrompterHost host = await Configured();

        await host.ListImageModels(null, CancellationToken.None);
        await host.ListImageModels("http://gpu:7860", CancellationToken.None);

        Assert.Equal([new Uri("http://localhost:7860"), new Uri("http://gpu:7860")], imageAsked);
    }

    [Fact]
    public async Task ListModels_RefusesAnAddressThatIsNotHttp()
    {
        PrompterHost host = await Configured();

        await Assert.ThrowsAsync<RequestException>(() => host.ListLocalModels("localhost:11434", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => host.ListImageModels("file:///x", CancellationToken.None));
        Assert.Empty(localAsked);
    }

    [Fact]
    public async Task ListLocalModels_ReportsAServerThatCannotBeReached()
    {
        PrompterHost host = await Configured();
        failure = new HttpRequestException("Connection refused");

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => host.ListLocalModels(null, CancellationToken.None));

        Assert.Contains("Connection refused", exception.Message);
    }

    [Fact]
    public async Task Set_StoresTheEnvironmentWithAVersionAndConfigureReadsItBack()
    {
        PrompterHost host = await Configured();
        await host.Set(new AiEnvironment { DefaultService = AiService.Codex }, CancellationToken.None);

        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "environment.json")))!["version"]!.GetValue<int>());
        Assert.Equal(AiService.Codex, (await Configured()).DefaultService);
    }

    [Fact]
    public async Task Configure_TakesAnEnvironmentStoredBeforeFilesWereVersionedForTheInitialVersionAndStampsItWhenItIsNextWritten()
    {
        string path = Path.Combine(temp.Path, "environment.json");
        await File.WriteAllTextAsync(path, """{ "defaultService": "Codex", "claudeCommand": "/opt/claude" }""");

        PrompterHost host = await Configured();

        Assert.Equal((AiService.Codex, "/opt/claude"), (host.DefaultService, host.Get().ClaudeCommand));
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bak"));
        await host.Set(host.Get(), CancellationToken.None);
        Assert.Equal(1, JsonNode.Parse(await File.ReadAllTextAsync(path))!["version"]!.GetValue<int>());
    }

    [Fact]
    public async Task Configure_RefusesToStartOnAnEnvironmentANewerReleaseStoredAndLeavesItAlone()
    {
        string path = Path.Combine(temp.Path, "environment.json");
        await File.WriteAllTextAsync(path, """{ "version": 7, "defaultService": "Codex" }""");

        await Assert.ThrowsAsync<NewerFileException>(Configured);

        Assert.Equal("""{ "version": 7, "defaultService": "Codex" }""", await File.ReadAllTextAsync(path));
    }

    private (PrompterHost Host, FakePromptSession Session, Mock<IPrompter> Prompter) Stored(ImageGenerationKind images = ImageGenerationKind.None)
    {
        FakePromptSession session = new();
        Mock<IPrompter> mock = new();
        mock.SetupGet(x => x.Environment).Returns(new PromptEnvironment());
        mock.SetupSet(x => x.Environment = It.IsAny<PromptEnvironment>());
        mock.Setup(x => x.Load(session.Id, It.IsAny<CancellationToken>())).ReturnsAsync(session);
        File.WriteAllText(Path.Combine(temp.Path, "environment.json"), $$"""{ "imageGeneration": "{{images}}", "openAiApiKey": "sk-image" }""");
        return (new PrompterHost(_ => mock.Object), session, mock);
    }

    [Fact]
    public async Task Adopt_GivesTheSessionWhatTheServerAlwaysRanItsSessionsWithAndClosesIt()
    {
        (PrompterHost host, FakePromptSession session, _) = Stored(ImageGenerationKind.OpenAi);
        await host.Configure(Config, CancellationToken.None);

        await host.Adopt(session.Id, CancellationToken.None);

        IPromptSessionConfiguration configuration = session.Configuration;
        Assert.Equal((true, true, true, true), (configuration.IsShellAllowed, configuration.IsWebAllowed, configuration.IsSystemAccessAllowed, configuration.IsImageGenerationAllowed));
        Assert.Equal(builtIn.Compose(null, 5), configuration.Instructions);
        Assert.Contains("in the foreground", configuration.Instructions);
        Assert.Contains("status update", configuration.Instructions);
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public async Task Adopt_KeepsTheWorkspaceWorkingAsItDidWhateverTheNewWorkspaceDefaultsSay()
    {
        (PrompterHost host, FakePromptSession session, _) = Stored();
        await host.Configure(Config, CancellationToken.None);
        await host.Set(host.Get() with { NewWorkspaces = new WorkspaceDefaults { IsShellAllowed = false, IsWebAllowed = false, IsSystemAccessAllowed = false, IsImageGenerationAllowed = true, Instructions = "be brief" } }, CancellationToken.None);

        await host.Adopt(session.Id, CancellationToken.None);

        IPromptSessionConfiguration configuration = session.Configuration;
        Assert.Equal((true, true, true, false), (configuration.IsShellAllowed, configuration.IsWebAllowed, configuration.IsSystemAccessAllowed, configuration.IsImageGenerationAllowed));
        Assert.Contains("in the foreground", configuration.Instructions);
    }

    [Fact]
    public async Task Adopt_ReportsASessionThatCannotBeLoaded()
    {
        (PrompterHost host, FakePromptSession session, Mock<IPrompter> mock) = Stored();
        mock.Setup(x => x.Load(session.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new DirectoryNotFoundException("The workspace folder does not exist."));
        await host.Configure(Config, CancellationToken.None);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => host.Adopt(session.Id, CancellationToken.None));

        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public async Task Configure_AppliesTheNewWorkspaceDefaultsToTheEnvironmentOfThePrompter()
    {
        PrompterHost host = await Configured();

        PromptEnvironment environment = prompter.Environment;
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High, false, false, true, false), (environment.Capability, environment.Effort, environment.IsShellAllowed, environment.IsSystemAccessAllowed, environment.IsWebAllowed, environment.IsImageGenerationAllowed));
        Assert.Equal(builtIn.Compose(null, 5), environment.Instructions);
        Assert.Equal(string.Empty, host.Get().NewWorkspaces.Instructions);
    }

    [Fact]
    public async Task Set_AppliesTheNewWorkspaceDefaultsAndKeepsThemForTheNextStart()
    {
        PrompterHost host = await Configured();
        WorkspaceDefaults defaults = new() { Capability = AiCapability.Max, Effort = AiEffort.Low, IsShellAllowed = false, IsSystemAccessAllowed = false, IsWebAllowed = false, IsImageGenerationAllowed = false, Instructions = "  be brief  " };

        AiEnvironment saved = await host.Set(host.Get() with { NewWorkspaces = defaults }, CancellationToken.None);

        PromptEnvironment environment = prompter.Environment;
        Assert.Equal((PromptCapability.Max, PromptEffort.Low, false, false, false, false, builtIn.Compose("be brief", 5)), (environment.Capability, environment.Effort, environment.IsShellAllowed, environment.IsSystemAccessAllowed, environment.IsWebAllowed, environment.IsImageGenerationAllowed, environment.Instructions));
        Assert.Equal(defaults with { Instructions = "be brief" }, saved.NewWorkspaces);
        Assert.Equal(saved.NewWorkspaces, (await Configured()).Get().NewWorkspaces);
    }

    [Fact]
    public async Task Set_TurnsBlankNewWorkspaceInstructionsIntoNoneAndRefusesACapabilityTheAiDoesNotKnow()
    {
        PrompterHost host = await Configured();

        AiEnvironment saved = await host.Set(host.Get() with { NewWorkspaces = new WorkspaceDefaults { Instructions = "   " } }, CancellationToken.None);

        Assert.Equal(string.Empty, saved.NewWorkspaces.Instructions);
        Assert.Equal(builtIn.Compose(null, 5), prompter.Environment.Instructions);
        await Assert.ThrowsAsync<RequestException>(() => host.Set(host.Get() with { NewWorkspaces = new WorkspaceDefaults { Capability = (AiCapability)99 } }, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => host.Set(host.Get() with { NewWorkspaces = new WorkspaceDefaults { Effort = (AiEffort)99 } }, CancellationToken.None));
    }

    [Fact]
    public async Task Configure_StartsAnEnvironmentStoredBeforeTheNewWorkspaceDefaultsExistedWithTheOriginalDefaults()
    {
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "environment.json"), """{ "defaultService": "Codex" }""");

        PrompterHost host = await Configured();

        Assert.Equal(new WorkspaceDefaults(), host.Get().NewWorkspaces);
    }

    [Fact]
    public async Task Configure_LeavesSessionsThatAreNotUpgradedAlone()
    {
        await prompter.Create(new ClaudeSetup());

        await Configured();

        Assert.False(prompter.Sessions[0].Configuration.IsShellAllowed);
        Assert.Null(prompter.Sessions[0].Configuration.Instructions);
    }

    [Fact]
    public async Task Configure_TakesTheAiSettingsOfAnOlderConfigOverOnceAndStoresThem()
    {
        PrompterHost host = Host();

        await host.Configure(OldConfig("""{ "key": "k", "ClaudeCommand": " /opt/claude ", "codexCommand": "/opt/codex", "openCodeCommand": "/opt/opencode", "localModelUrl": "http://gpu:1234/v1" }"""), CancellationToken.None);

        AiEnvironment environment = host.Get();
        Assert.Equal(("/opt/claude", "/opt/codex", "/opt/opencode", "http://gpu:1234/v1"), (environment.ClaudeCommand, environment.CodexCommand, environment.LocalCommand, environment.LocalServer));
        Assert.Equal("/opt/claude", prompter.Environment.ClaudeCommand);
        PrompterHost next = Host();
        await next.Configure(Config, CancellationToken.None);
        Assert.Equal("/opt/codex", next.Get().CodexCommand);
    }

    [Fact]
    public async Task Configure_IgnoresTheAiSettingsOfTheConfigOnceAnEnvironmentIsStored()
    {
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "environment.json"), """{ "claudeCommand": "/stored/claude" }""");
        PrompterHost host = Host();

        await host.Configure(OldConfig("""{ "key": "k", "claudeCommand": "/old/claude" }"""), CancellationToken.None);

        Assert.Equal("/stored/claude", host.Get().ClaudeCommand);
    }

    [Fact]
    public async Task Configure_KeepsDefaultsForOldAiSettingsThatMakeNoSense()
    {
        PrompterHost host = Host();

        await host.Configure(OldConfig("""{ "key": "k", "claudeCommand": " ", "localModelUrl": "localhost:11434", "somethingElse": 3 }"""), CancellationToken.None);

        Assert.Equal(new AiEnvironment(), host.Get());
        Assert.False(File.Exists(Path.Combine(temp.Path, "environment.json")));
    }

    [Fact]
    public async Task CreateSession_MakesASessionInTheFolderThatStartsWithWhatTheEnvironmentSaysAndAnswersItsIdentifier()
    {
        PrompterHost host = await Configured();
        string folder = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(folder);

        string id = await host.CreateSession(folder, AiService.Codex, " sk-1 ", CancellationToken.None);

        CodexSetup setup = Assert.IsType<CodexSetup>(Assert.Single(prompter.Created));
        Assert.Equal((folder, "sk-1"), (setup.WorkspacePath, setup.ApiKey));
        IPromptSessionConfiguration configuration = prompter.Sessions[0].Configuration;
        Assert.Equal((PromptCapability.Balanced, PromptEffort.High), (configuration.Capability, configuration.Effort));
        Assert.Equal((false, false, true, false), (configuration.IsShellAllowed, configuration.IsSystemAccessAllowed, configuration.IsWebAllowed, configuration.IsImageGenerationAllowed));
        Assert.Equal(builtIn.Compose(null, 5), configuration.Instructions);
        Assert.Equal(prompter.Sessions[0].Id, id);
        Assert.True(prompter.Sessions[0].IsDisposed);
    }

    [Theory]
    [InlineData(AiService.ClaudeCode, typeof(ClaudeSetup))]
    [InlineData(AiService.Codex, typeof(CodexSetup))]
    [InlineData(AiService.Local, typeof(LocalSetup))]
    public async Task CreateSession_UsesTheSetupOfTheProvider(AiService service, Type expected)
    {
        PrompterHost host = await Configured();

        await host.CreateSession(temp.Path, service, null, CancellationToken.None);

        Assert.IsType(expected, Assert.Single(prompter.Created));
    }

    [Fact]
    public async Task CreateSession_StartsTheSessionWithTheNewWorkspaceDefaultsThatAreSet()
    {
        PrompterHost host = await Configured();
        await host.Set(host.Get() with { NewWorkspaces = new WorkspaceDefaults { Capability = AiCapability.Strong, Effort = AiEffort.Medium, IsShellAllowed = false, IsImageGenerationAllowed = false, Instructions = "be brief" } }, CancellationToken.None);

        await host.CreateSession(temp.Path, AiService.ClaudeCode, null, CancellationToken.None);

        IPromptSessionConfiguration configuration = prompter.Sessions.Single().Configuration;
        Assert.Equal((PromptCapability.Strong, PromptEffort.Medium, false, false, builtIn.Compose("be brief", 5)), (configuration.Capability, configuration.Effort, configuration.IsShellAllowed, configuration.IsImageGenerationAllowed, configuration.Instructions));
    }

    [Fact]
    public async Task CreateSession_RefusesAKeyForTheLocalProviderAndMakesNoSession()
    {
        PrompterHost host = await Configured();

        await Assert.ThrowsAsync<RequestException>(() => host.CreateSession(temp.Path, AiService.Local, "sk-1", CancellationToken.None));

        Assert.Empty(prompter.Sessions);
    }

    [Fact]
    public async Task CreateSession_ReportsWhyTheLibraryCannotMakeTheSession()
    {
        Mock<IPrompter> broken = new();
        broken.SetupGet(x => x.Environment).Returns(new PromptEnvironment());
        broken.Setup(x => x.List(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        broken.Setup(x => x.Create(It.IsAny<PromptSetup>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ArgumentException("Local models need a model for every capability."));
        PrompterHost host = new(_ => broken.Object);
        await host.Configure(Config, CancellationToken.None);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => host.CreateSession(temp.Path, AiService.Local, null, CancellationToken.None));

        Assert.Contains("Local models need a model", exception.Message);
    }

    [Fact]
    public async Task MoveSession_TellsLiveAiTheSessionIsInTheFolderNowWithoutLoadingIt()
    {
        PrompterHost host = await Configured();
        string id = await host.CreateSession(temp.Path, AiService.ClaudeCode, null, CancellationToken.None);
        string moved = Path.Combine(temp.Path, "moved");
        Directory.CreateDirectory(moved);

        await host.MoveSession(id, moved, CancellationToken.None);

        Assert.Equal(moved, prompter.Sessions[0].WorkspacePath);
        Assert.Equal(moved, Assert.Single(await prompter.List()).WorkspacePath);
    }

    [Fact]
    public async Task MoveSession_ReportsASessionThatIsNotThereOrAFolderThatIsNot()
    {
        PrompterHost host = await Configured();
        string id = await host.CreateSession(temp.Path, AiService.ClaudeCode, null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => host.MoveSession("0123456789abcdef0123456789abcdef", temp.Path, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => host.MoveSession(id, Path.Combine(temp.Path, "missing"), CancellationToken.None));
    }

    [Fact]
    public async Task Set_RefusesAMissingCommandWithAMessageAndTreatsMissingListsAsEmptyInsteadOfFailing()
    {
        PrompterHost host = await Configured();
        AiEnvironment good = host.Get();

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => host.Set(good with { ClaudeCommand = null! }, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => host.Set(good with { LocalServer = null! }, CancellationToken.None));
        AiEnvironment saved = await host.Set(good with { LocalModels = null!, LocalImageModels = null!, NewWorkspaces = null! }, CancellationToken.None);

        Assert.Contains("command", exception.Message);
        Assert.Equal(new CapabilityNames(), saved.LocalModels);
        Assert.Equal(new WorkspaceDefaults(), saved.NewWorkspaces);
    }
}
