namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SettingsViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", Service = AiService.Codex };

    private readonly List<WorkspaceSettings> saved = [];

    private WorkspaceSettings Current => new()
    {
        Service = AiService.Codex,
        Capability = AiCapability.Balanced,
        Effort = AiEffort.High,
        IsShellAllowed = true,
        IsSystemAccessAllowed = true,
        IsWebAllowed = true,
        Instructions = "work in the foreground",
        Descriptors = new Dictionary<string, string> { ["Provider"] = "Codex", ["Model"] = "GPT 5.6" }
    };

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private void Session(WorkspaceSettings settings) => connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(settings);

    private void Accept() => connection.Setup(x => x.SetWorkspaceSettings(workspace.Id, It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, WorkspaceSettings given, CancellationToken _) =>
    {
        saved.Add(given);
        return given;
    });

    private SettingsViewModel Settings() => new(connection.Object, workspace);

    [Fact]
    public async Task Opening_ShowsTheProviderOfTheWorkspaceAndTheSettingsOfItsAiSession()
    {
        Session(Current);

        SettingsViewModel settings = Settings();
        await Until(() => settings.Instructions.Length > 0);

        Assert.Equal("OpenAI Codex", settings.ProviderName);
        Assert.Equal(AiCapability.Balanced, settings.SelectedCapability.Value);
        Assert.Equal(AiEffort.High, settings.SelectedEffort.Value);
        Assert.Equal((true, true, true, false), (settings.IsShellAllowed, settings.IsSystemAccessAllowed, settings.IsWebAllowed, settings.IsImageGenerationAllowed));
        Assert.Equal("work in the foreground", settings.Instructions);
        Assert.False(settings.HasPending);
        Assert.Equal(["Provider", "Model"], settings.Details.Select(line => line.Name));
        Assert.True(settings.HasDetails);
    }

    [Fact]
    public async Task Choices_ListEveryCapabilityAndEffort()
    {
        Session(Current);

        SettingsViewModel settings = Settings();

        Assert.Equal(["Fast", "Balanced", "Strong", "Max"], settings.Capabilities.Select(choice => choice.Name));
        Assert.Equal(["Low", "Medium", "High", "Max"], settings.Efforts.Select(choice => choice.Name));
        await Until(() => settings.HasDetails);
    }

    [Fact]
    public async Task Reloading_ReadsTheSettingsAgainSoAChangeFromAnotherClientShows()
    {
        Session(Current);
        SettingsViewModel settings = Settings();
        await Until(() => settings.SelectedEffort.Value == AiEffort.High);
        Session(Current with { Effort = AiEffort.Low, Capability = AiCapability.Max });

        await settings.Reload();

        Assert.Equal((AiCapability.Max, AiEffort.Low), (settings.SelectedCapability.Value, settings.SelectedEffort.Value));
    }

    [Fact]
    public async Task Edits_ArePendingAndNothingIsSentUntilTheyAreApplied()
    {
        Session(Current);
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.HasDetails);

        settings.SelectedCapability = settings.Capabilities.Single(choice => choice.Value == AiCapability.Strong);
        settings.SelectedEffort = settings.Efforts.Single(choice => choice.Value == AiEffort.Max);
        settings.IsShellAllowed = false;
        settings.IsSystemAccessAllowed = false;
        settings.IsWebAllowed = false;
        settings.IsImageGenerationAllowed = true;
        settings.Instructions = "be brief";

        Assert.True(settings.HasPending);
        Assert.Empty(saved);
        Assert.Equal((AiCapability.Strong, AiEffort.Max, false, false, false, true, "be brief"), (settings.SelectedCapability.Value, settings.SelectedEffort.Value, settings.IsShellAllowed, settings.IsSystemAccessAllowed, settings.IsWebAllowed, settings.IsImageGenerationAllowed, settings.Instructions));
    }

    [Fact]
    public async Task Apply_SendsEveryEditTogetherOnceAndClearsWhatWasPending()
    {
        Session(Current);
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.HasDetails);
        settings.SelectedCapability = settings.Capabilities.Single(choice => choice.Value == AiCapability.Strong);
        settings.IsShellAllowed = false;
        settings.Instructions = "  be brief  ";

        bool applied = await settings.Apply();

        Assert.True(applied);
        WorkspaceSettings sent = Assert.Single(saved);
        Assert.Equal((AiCapability.Strong, AiEffort.High, false, true, true, "be brief"), (sent.Capability, sent.Effort, sent.IsShellAllowed, sent.IsSystemAccessAllowed, sent.IsWebAllowed, sent.Instructions));
        Assert.False(settings.HasPending);
        Assert.Equal("be brief", settings.Instructions);
    }

    [Fact]
    public async Task Prompts_ShowTheOverridesOfTheWorkspaceNextToTheGlobalPromptsAndApplyWithTheOtherEdits()
    {
        Session(Current with { ReviewPrompt = "own review", GlobalReviewPrompt = "global review", GlobalExpandPrompt = "global expand" });
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.Instructions.Length > 0);

        Assert.Equal(("own review", string.Empty, "global review", "global expand"), (settings.ReviewPrompt, settings.ExpandPrompt, settings.GlobalReviewPrompt, settings.GlobalExpandPrompt));
        Assert.False(settings.HasPending);

        settings.ExpandPrompt = "  grow it  ";
        Assert.True(settings.HasPending);
        Assert.True(await settings.Apply());

        Assert.Equal(("own review", "grow it"), (saved.Single().ReviewPrompt, saved.Single().ExpandPrompt));
    }

    [Fact]
    public async Task ClearingAPrompt_SendsNoOverrideSoTheGlobalPromptIsUsedAgain()
    {
        Session(Current with { ReviewPrompt = "own review", ExpandPrompt = "own expand", GlobalReviewPrompt = "global review" });
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.ReviewPrompt.Length > 0);
        List<string?> changed = [];
        settings.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        settings.ClearReviewPromptCommand.Execute(null);
        settings.ExpandPrompt = "   ";

        Assert.Equal((string.Empty, true), (settings.ReviewPrompt, settings.HasPending));
        Assert.Contains(nameof(SettingsViewModel.ReviewPrompt), changed);
        Assert.True(await settings.Apply());
        Assert.Equal((null, null), (saved.Single().ReviewPrompt, saved.Single().ExpandPrompt));

        settings.ClearExpandPromptCommand.Execute(null);
        Assert.False(settings.HasPending);
    }

    [Fact]
    public async Task Apply_WithoutEditsOrWithEditsThatWereUndoneSendsNothing()
    {
        Session(Current);
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.HasDetails);
        Assert.True(await settings.Apply());

        settings.IsShellAllowed = false;
        settings.IsShellAllowed = true;
        settings.Instructions = "work in the foreground  ";

        Assert.False(settings.HasPending);
        Assert.True(await settings.Apply());
        Assert.Empty(saved);
    }

    [Fact]
    public async Task Apply_WhenTheServerRefuses_ShowsTheReasonAndKeepsTheEditsPending()
    {
        Session(Current);
        connection.Setup(x => x.SetWorkspaceSettings(workspace.Id, It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("System access cannot be turned off with this provider."));
        SettingsViewModel settings = Settings();
        await Until(() => settings.HasDetails);
        settings.IsSystemAccessAllowed = false;

        bool applied = await settings.Apply();

        Assert.False(applied);
        Assert.Equal("System access cannot be turned off with this provider.", settings.Error);
        Assert.True(settings.HasPending);
        Assert.False(settings.IsSystemAccessAllowed);
    }

    [Fact]
    public async Task ALoadThatFinishesAfterAnEdit_KeepsTheEditAndOnlyBringsInWhatTheSessionSays()
    {
        TaskCompletionSource<WorkspaceSettings> loading = new();
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).Returns(loading.Task);
        SettingsViewModel settings = Settings();

        settings.IsShellAllowed = false;
        loading.SetResult(Current);
        await Until(() => settings.HasDetails);

        Assert.False(settings.IsShellAllowed);
        Assert.True(settings.HasPending);
        Assert.Equal(["Provider", "Model"], settings.Details.Select(line => line.Name));
    }

    [Fact]
    public async Task Reload_KeepsEditsThatWereNotAppliedAndOtherwiseReadsTheSessionAgain()
    {
        Session(Current);
        SettingsViewModel settings = Settings();
        await Until(() => settings.HasDetails);
        Session(Current with { Effort = AiEffort.Low });

        await settings.Reload();
        Assert.Equal(AiEffort.Low, settings.SelectedEffort.Value);

        settings.IsShellAllowed = false;
        Session(Current with { Effort = AiEffort.Max });
        await settings.Reload();

        Assert.Equal((AiEffort.Low, false), (settings.SelectedEffort.Value, settings.IsShellAllowed));
    }

    [Fact]
    public async Task ASessionThatCannotBeRead_ShowsTheReason()
    {
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Codex could not be started."));

        SettingsViewModel settings = Settings();
        await Until(() => settings.HasError);

        Assert.Equal("Codex could not be started.", settings.Error);
        Assert.False(settings.HasDetails);
    }
}
