namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SettingsViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly List<(AiService Service, string Model, Dictionary<string, string> Values)> saved = [];

    private ModelSettingInfo Effort(params string[] choices) => new() { Id = "effort", Name = "Effort", Kind = ModelSettingKind.Choice, Choices = choices, Default = "medium" };

    private ModelSettingInfo Thinking => new() { Id = "thinking", Name = "Thinking", Kind = ModelSettingKind.Toggle, Default = "false" };

    private ModelSettingInfo Context => new() { Id = "context", Name = "Context window", Kind = ModelSettingKind.Number, Minimum = 1024, Maximum = 262144, Default = "4096" };

    private ModelInfo Sonnet => new() { Id = "sonnet", Name = "Sonnet 5.5", Description = "Most efficient", Settings = [Effort("low", "medium", "high", "max")] };

    private ModelInfo Opus => new() { Id = "opus", Name = "Opus 5.5", Settings = [Effort("low", "medium", "high", "xhigh", "max"), Thinking] };

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 150 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private void Offer(AiService service, params ModelInfo[] models) => connection.Setup(x => x.ListModels(service, It.IsAny<CancellationToken>())).ReturnsAsync(models);

    private void Session(string? model, params (string Id, string Value)[] values) => connection.Setup(x => x.GetWorkspaceAi(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceAi { Model = model, ModelSettings = values.ToDictionary(pair => pair.Id, pair => pair.Value) });

    private void Accept() => connection.Setup(x => x.ConfigureWorkspace(workspace.Id, It.IsAny<AiService>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, AiService service, string model, IReadOnlyDictionary<string, string> values, CancellationToken _) =>
    {
        saved.Add((service, model, new Dictionary<string, string>(values)));
        return workspace with { Service = service };
    });

    private SettingsViewModel Settings() => new(connection.Object, workspace);

    [Fact]
    public async Task Opening_ListsTheModelsOfTheWorkspacesServiceSelectsTheModelOfItsAiSessionAndShowsItsSettingsWithTheSessionsValues()
    {
        Offer(AiService.ClaudeCode, Sonnet, Opus);
        Session("sonnet", ("effort", "high"));

        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        Assert.Equal(["Sonnet 5.5", "Opus 5.5"], settings.Models.Select(choice => choice.Name));
        Assert.Equal("sonnet", settings.SelectedModel?.Id);
        Assert.Equal("Most efficient", settings.ModelDescription);
        Assert.True(settings.HasSettings);
        ModelSettingViewModel effort = settings.Settings.Single();
        Assert.Equal(("Effort", true, "high"), (effort.Name, effort.IsChoice, effort.ChoiceValue));
        Assert.Equal(["low", "medium", "high", "max"], effort.Choices);
    }

    [Fact]
    public async Task Reloading_ReadsTheModelAndSettingsOfTheSessionAgainSoAChangeFromAnotherClientShows()
    {
        Offer(AiService.ClaudeCode, Sonnet, Opus);
        Session("sonnet", ("effort", "high"));
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);
        Session("opus", ("effort", "max"), ("thinking", "true"));

        await settings.Reload();

        Assert.Equal("opus", settings.SelectedModel?.Id);
        Assert.Equal(["max", "true"], settings.Settings.Select(setting => setting.IsToggle ? setting.ToggleValue.ToString().ToLowerInvariant() : setting.ChoiceValue));
    }

    [Fact]
    public async Task ChangingASetting_SavesTheModelWithAllItsValues()
    {
        Offer(AiService.ClaudeCode, Sonnet, Opus);
        Session("sonnet", ("effort", "high"));
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.Settings.Single().ChoiceValue = "max";
        await Until(() => saved.Count == 1);

        Assert.Equal((AiService.ClaudeCode, "sonnet", "max"), (saved[0].Service, saved[0].Model, saved[0].Values["effort"]));
    }

    [Fact]
    public async Task ChoosingAnotherModel_KeepsTheValuesOfSettingsBothModelsHaveGivesTheRestTheirDefaultsAndSaves()
    {
        Offer(AiService.ClaudeCode, Sonnet, Opus);
        Session("sonnet", ("effort", "high"));
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.SelectedModel = settings.Models.Single(choice => choice.Id == "opus");
        await Until(() => saved.Count == 1);

        Assert.Equal("opus", saved[0].Model);
        Assert.Equal(new Dictionary<string, string> { ["effort"] = "high", ["thinking"] = "false" }, saved[0].Values);
        Assert.Equal(["Effort", "Thinking"], settings.Settings.Select(setting => setting.Name));
        Assert.True(settings.Settings[1].IsToggle);
    }

    [Fact]
    public async Task ChoosingAModelWhoseSettingDoesNotTakeTheCurrentValue_UsesTheDefaultInstead()
    {
        ModelInfo old = new() { Id = "old", Settings = [Effort("low", "medium")] };
        Offer(AiService.ClaudeCode, Sonnet, old);
        Session("sonnet", ("effort", "max"));
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.SelectedModel = settings.Models.Single(choice => choice.Id == "old");
        await Until(() => saved.Count == 1);

        Assert.Equal("medium", saved[0].Values["effort"]);
    }

    [Fact]
    public async Task SelectingAnotherService_ListsItsModelsPicksItsDefaultOrTheFirstAndSaves()
    {
        Offer(AiService.ClaudeCode, Sonnet);
        Session("sonnet", ("effort", "high"));
        Offer(AiService.Codex, new ModelInfo { Id = "gpt-5.6-luna", Settings = [Effort("low", "medium")] }, new ModelInfo { Id = "gpt-5.6-terra", Settings = [Effort("low", "medium", "high")] });
        Accept();
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.SelectedService = settings.Services.Single(choice => choice.Service == AiService.Codex);
        await Until(() => saved.Count == 1);

        Assert.Equal(AiService.Codex, settings.SelectedService.Service);
        Assert.Equal((AiService.Codex, "gpt-5.6-terra", "medium"), (saved[0].Service, saved[0].Model, saved[0].Values["effort"]));
        Assert.Equal("gpt-5.6-terra", settings.SelectedModel?.Id);
        Assert.Equal(["gpt-5.6-luna", "gpt-5.6-terra"], settings.Models.Select(choice => choice.Id));
    }

    [Fact]
    public async Task SelectingAServiceWithoutModels_StaysOnTheCurrentServiceAndSaysWhy()
    {
        Offer(AiService.ClaudeCode, Sonnet);
        Session("sonnet");
        Offer(AiService.OpenCode);
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.SelectedService = settings.Services.Single(choice => choice.Service == AiService.OpenCode);
        await Until(() => settings.HasError);

        Assert.Equal(AiService.ClaudeCode, settings.SelectedService.Service);
        Assert.Contains("OpenCode offers no models", settings.Error);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task ASessionWhoseModelTheServiceNoLongerOffers_StillShowsItAmongTheChoicesAndASessionWithoutOneShowsNoSelection()
    {
        Offer(AiService.ClaudeCode, Opus);
        Session("retired");
        SettingsViewModel retired = Settings();
        await Until(() => retired.Models.Count == 2);

        Session(null);
        SettingsViewModel none = Settings();
        await Until(() => none.Models.Count == 1);

        Assert.Equal(["retired", "opus"], retired.Models.Select(choice => choice.Id));
        Assert.Equal("retired", retired.SelectedModel?.Id);
        Assert.False(retired.HasSettings);
        Assert.Null(none.SelectedModel);
    }

    [Fact]
    public async Task AModelTheServerRefuses_GoesBackToTheSessionsModelAndKeepsTheReason()
    {
        Offer(AiService.ClaudeCode, Sonnet, Opus);
        Session("sonnet", ("effort", "high"));
        connection.Setup(x => x.ConfigureWorkspace(workspace.Id, It.IsAny<AiService>(), "opus", It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Claude Code does not offer the model opus."));
        SettingsViewModel settings = Settings();
        await Until(() => settings.Settings.Count == 1);

        settings.SelectedModel = settings.Models.Single(choice => choice.Id == "opus");
        await Until(() => settings.HasError && settings.SelectedModel?.Id == "sonnet");

        Assert.Contains("does not offer the model opus", settings.Error);
        Assert.Equal("high", settings.Settings.Single().ChoiceValue);
    }

    [Fact]
    public async Task RefreshingTheModels_ReadsThemFromTheServerAgain()
    {
        Offer(AiService.ClaudeCode, Sonnet);
        Session("sonnet");
        SettingsViewModel settings = Settings();
        await Until(() => settings.Models.Count == 1);
        Offer(AiService.ClaudeCode, Sonnet, Opus);

        settings.RefreshModelsCommand.Execute(null);
        await Until(() => settings.Models.Count == 2);

        Assert.Equal(["sonnet", "opus"], settings.Models.Select(choice => choice.Id));
    }

    [Fact]
    public void ASwitchSetting_IsOnlyOnForTrueAndWritesTrueOrFalse()
    {
        List<(string Id, string Value)> changes = [];
        ModelSettingViewModel setting = new(Thinking, "false", (id, value) => changes.Add((id, value)));

        setting.ToggleValue = true;
        setting.ToggleValue = true;
        setting.ToggleValue = false;

        Assert.True(setting.IsToggle);
        Assert.False(setting.ToggleValue);
        Assert.Equal([("thinking", "true"), ("thinking", "false")], changes);
    }

    [Fact]
    public void ANumberSetting_WritesWholeNumbersInItsRangeAndIgnoresTheRest()
    {
        List<(string Id, string Value)> changes = [];
        ModelSettingViewModel setting = new(Context, "4096", (id, value) => changes.Add((id, value)));

        setting.NumberValue = 16384.7m;
        setting.NumberValue = 10;
        setting.NumberValue = 999999999;
        setting.NumberValue = null;

        Assert.True(setting.IsNumber);
        Assert.Equal((1024m, 262144m, 1024m), (setting.Minimum, setting.Maximum, setting.Increment));
        Assert.Equal(16384m, setting.NumberValue);
        Assert.Equal([("context", "16384")], changes);
    }
}
