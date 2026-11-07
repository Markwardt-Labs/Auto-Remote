namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class EnvironmentViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly List<AiEnvironment> saved = [];

    private AiEnvironment Current => new()
    {
        DefaultService = AiService.Codex,
        ClaudeCommand = "claude",
        ClaudeConfigFolder = "/home/me/.claude-work",
        HasClaudeApiKey = true,
        CodexCommand = "codex",
        LocalCommand = "opencode",
        LocalServer = "http://localhost:11434/v1",
        LocalModels = new CapabilityNames { Fast = "small", Balanced = "mid", Strong = "big", Max = "huge" },
        LocalContexts = new CapabilityContexts { Fast = 8192, Max = 65536 },
        ImageGeneration = ImageGenerationKind.Local,
        LocalImageServer = "http://localhost:7860",
        LocalImageModels = new CapabilityNames { Fast = "a", Balanced = "b", Strong = "c", Max = "d" },
        NewWorkspaces = new WorkspaceDefaults { Capability = AiCapability.Strong, Effort = AiEffort.Low, IsShellAllowed = false, IsSystemAccessAllowed = true, IsWebAllowed = false, IsImageGenerationAllowed = true, Instructions = "be brief" }
    };

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private bool closed;

    private EnvironmentViewModel Open(AiEnvironment? environment = null)
    {
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(environment ?? Current);
        connection.Setup(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>())).ReturnsAsync((AiEnvironment given, CancellationToken _) =>
        {
            saved.Add(given);
            return given with { ClaudeApiKey = null, CodexApiKey = null, OpenAiApiKey = null, HasClaudeApiKey = !string.IsNullOrEmpty(given.ClaudeApiKey) || (environment ?? Current).HasClaudeApiKey && given.ClaudeApiKey != string.Empty };
        });
        EnvironmentViewModel model = new(connection.Object);
        model.Closed += () => closed = true;
        return model;
    }

    [Fact]
    public async Task Load_ShowsEverythingTheEnvironmentHolds()
    {
        EnvironmentViewModel model = Open();

        await model.Load();

        Assert.Equal(AiService.Codex, model.SelectedDefault.Service);
        Assert.Equal(("claude", "/home/me/.claude-work", true), (model.ClaudeCommand, model.ClaudeConfigFolder, model.HasClaudeApiKey));
        Assert.Equal(string.Empty, model.ClaudeApiKey);
        Assert.Equal(("opencode", "http://localhost:11434/v1"), (model.LocalCommand, model.LocalServer));
        Assert.Equal(["small", "mid", "big", "huge"], model.LocalRows.Select(row => row.Model));
        Assert.Equal([8192m, null, null, 65536m], model.LocalRows.Select(row => row.Context));
        Assert.Equal(ImageGenerationKind.Local, model.SelectedImage.Value);
        Assert.True(model.UsesLocalImages);
        Assert.False(model.UsesOpenAi);
        Assert.Equal(["a", "b", "c", "d"], model.ImageRows.Select(row => row.Model));
    }

    [Fact]
    public async Task Load_ShowsWhatNewWorkspacesStartWith()
    {
        EnvironmentViewModel model = Open();

        await model.Load();

        Assert.Equal((AiCapability.Strong, AiEffort.Low), (model.NewCapability.Value, model.NewEffort.Value));
        Assert.Equal((false, true, false, true, "be brief"), (model.NewIsShellAllowed, model.NewIsSystemAccessAllowed, model.NewIsWebAllowed, model.NewIsImageGenerationAllowed, model.NewInstructions));
        Assert.Equal(["Fast", "Balanced", "Strong", "Max"], model.Capabilities.Select(choice => choice.Name));
        Assert.Equal(["Low", "Medium", "High", "Max"], model.Efforts.Select(choice => choice.Name));
    }

    [Fact]
    public async Task Close_SendsTheEditedNewWorkspaceDefaultsWithBlankInstructionsAsEmpty()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.NewCapability = model.Capabilities.Single(choice => choice.Value == AiCapability.Max);
        model.NewEffort = model.Efforts.Single(choice => choice.Value == AiEffort.High);
        model.NewIsShellAllowed = true;
        model.NewIsSystemAccessAllowed = false;
        model.NewIsWebAllowed = true;
        model.NewIsImageGenerationAllowed = false;
        model.NewInstructions = "   ";

        model.CloseCommand.Execute(null);
        await Until(() => saved.Count == 1);

        Assert.Equal(new WorkspaceDefaults { Capability = AiCapability.Max, Effort = AiEffort.High, IsShellAllowed = true, IsSystemAccessAllowed = false, IsWebAllowed = true, IsImageGenerationAllowed = false, Instructions = string.Empty }, saved[0].NewWorkspaces);
    }

    [Fact]
    public async Task Close_KeepsTheNewWorkspaceDefaultsTheServerGivesBack()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.NewInstructions = "  changed  ";

        model.CloseCommand.Execute(null);
        await Until(() => closed);

        Assert.Equal("changed", saved[0].NewWorkspaces.Instructions);
        Assert.Equal("changed", model.NewInstructions);
    }

    [Fact]
    public async Task Close_SendsTheEditedValuesAndNoKeyThatWasNotTyped()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.SelectedDefault = model.Services.Single(choice => choice.Service == AiService.Local);
        model.ClaudeCommand = " /opt/claude ";
        model.LocalRows[0].Model = "tiny";
        model.LocalRows[0].Context = 4096;

        model.CloseCommand.Execute(null);
        await Until(() => saved.Count == 1);

        AiEnvironment sent = saved[0];
        Assert.Equal(AiService.Local, sent.DefaultService);
        Assert.Equal("/opt/claude", sent.ClaudeCommand);
        Assert.Null(sent.ClaudeApiKey);
        Assert.Null(sent.CodexApiKey);
        Assert.Equal("/home/me/.claude-work", sent.ClaudeConfigFolder);
        Assert.Null(sent.CodexConfigFolder);
        Assert.Equal(new CapabilityNames { Fast = "tiny", Balanced = "mid", Strong = "big", Max = "huge" }, sent.LocalModels);
        Assert.Equal(new CapabilityContexts { Fast = 4096, Max = 65536 }, sent.LocalContexts);
        await Until(() => closed);
    }

    [Fact]
    public async Task Close_SendsATypedKeyAndAnEmptyOneToRemoveAKeyThatIsSet()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.CodexApiKey = " sk-codex ";
        model.ClearClaudeApiKey = true;
        model.ClaudeApiKey = "ignored";

        model.CloseCommand.Execute(null);
        await Until(() => saved.Count == 1);

        Assert.Equal("sk-codex", saved[0].CodexApiKey);
        Assert.Equal(string.Empty, saved[0].ClaudeApiKey);
        Assert.Null(saved[0].OpenAiApiKey);
    }

    [Fact]
    public async Task Close_SendsNoContextsWhenNoneIsSet()
    {
        EnvironmentViewModel model = Open(Current with { LocalContexts = null });
        await model.Load();
        model.ClaudeCommand = "claude2";

        model.CloseCommand.Execute(null);
        await Until(() => saved.Count == 1);

        Assert.Null(saved[0].LocalContexts);
    }

    [Fact]
    public async Task Close_ClearsTheTypedKeysOnceTheServerHasThem()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.CodexApiKey = "sk-codex";

        model.CloseCommand.Execute(null);
        await Until(() => closed);

        Assert.Equal(string.Empty, model.CodexApiKey);
        Assert.False(model.ClearClaudeApiKey);
        Assert.True(closed);
    }

    [Fact]
    public async Task Close_ShowsWhyTheServerRefusedTheEnvironment()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        connection.Setup(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Local image generation needs a checkpoint for every capability."));
        model.SelectedImage = model.ImageKinds.Single(choice => choice.Value == ImageGenerationKind.OpenAi);

        model.CloseCommand.Execute(null);
        await Until(() => model.HasError);

        Assert.Equal("Local image generation needs a checkpoint for every capability.", model.Error);
        Assert.False(model.HasNotice);
        Assert.False(closed);
    }

    [Fact]
    public async Task Switching_TheImageServiceShowsItsOwnFields()
    {
        EnvironmentViewModel model = Open(Current with { ImageGeneration = ImageGenerationKind.None });
        await model.Load();

        Assert.False(model.UsesOpenAi || model.UsesLocalImages);
        model.SelectedImage = model.ImageKinds.Single(choice => choice.Value == ImageGenerationKind.OpenAi);
        Assert.True(model.UsesOpenAi);
        Assert.False(model.UsesLocalImages);
    }

    [Fact]
    public async Task RefreshLocalModels_AsksTheServerAddressOnScreenAndOffersWhatItFinds()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.LocalServer = " http://other:1234/v1 ";
        connection.Setup(x => x.ListLocalModels("http://other:1234/v1", It.IsAny<CancellationToken>())).ReturnsAsync(["gemma", "qwen"]);

        model.RefreshLocalModelsCommand.Execute(null);
        await Until(() => model.LocalModelNames.Count == 2);

        Assert.Equal(["gemma", "qwen"], model.LocalModelNames);
        Assert.Equal("Found 2 models.", model.Notice);
    }

    [Fact]
    public async Task RefreshLocalModels_SaysSoWhenTheServerOffersNone()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        connection.Setup(x => x.ListLocalModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        model.RefreshLocalModelsCommand.Execute(null);
        await Until(() => model.HasNotice);

        Assert.Equal("The server offers no models.", model.Notice);
    }

    [Fact]
    public async Task RefreshLocalModels_ShowsWhyTheServerCouldNotBeReached()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        connection.Setup(x => x.ListLocalModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The model server could not be reached."));

        model.RefreshLocalModelsCommand.Execute(null);
        await Until(() => model.HasError);

        Assert.Equal("The model server could not be reached.", model.Error);
        Assert.Empty(model.LocalModelNames);
    }

    [Fact]
    public async Task RefreshImageModels_AsksTheImageServerAddressOnScreenAndOffersWhatItFinds()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.LocalImageServer = "http://gpu:7860";
        connection.Setup(x => x.ListImageModels("http://gpu:7860", It.IsAny<CancellationToken>())).ReturnsAsync(["sdxl"]);

        model.RefreshImageModelsCommand.Execute(null);
        await Until(() => model.ImageModelNames.Count == 1);

        Assert.Equal(["sdxl"], model.ImageModelNames);
        Assert.Equal("Found 1 checkpoint.", model.Notice);
    }

    [Fact]
    public async Task Close_WithoutChangesSendsNothingAndCloses()
    {
        EnvironmentViewModel model = Open();
        await model.Load();

        model.CloseCommand.Execute(null);
        await Until(() => closed);

        connection.Verify(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_WithEditsThatWereUndoneSendsNothing()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.ClaudeCommand = "other";
        model.ClaudeCommand = "claude";

        model.CloseCommand.Execute(null);
        await Until(() => closed);

        connection.Verify(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_BeforeTheEnvironmentWasReadOnlyClosesSoNothingIsOverwritten()
    {
        EnvironmentViewModel model = Open();

        model.CloseCommand.Execute(null);
        await Until(() => closed);

        connection.Verify(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Close_StaysOpenWhenTheServerRefusesAndClosesOnceTheEditIsFixed()
    {
        EnvironmentViewModel model = Open();
        await model.Load();
        model.ClaudeCommand = " ";
        connection.Setup(x => x.SetAiEnvironment(It.Is<AiEnvironment>(sent => sent.ClaudeCommand.Length == 0), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The command of Claude Code is empty."));

        model.CloseCommand.Execute(null);
        await Until(() => model.HasError);
        Assert.False(closed);

        await Until(() => model.CloseCommand.CanExecute(null));
        model.ClaudeCommand = "claude2";
        model.CloseCommand.Execute(null);
        await Until(() => closed);

        Assert.False(model.HasError);
        Assert.Equal("claude2", saved.Single().ClaudeCommand);
    }
}
