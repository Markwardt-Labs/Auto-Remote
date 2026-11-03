namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The settings of one workspace: the AI service, the model of that service and the settings that model offers. The models come from the server and so do their settings, which are shown according to their kind; the model and the values the workspace's AI session runs with are read from the server too, which keeps no copy of its own.
/// </summary>
internal sealed class SettingsViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspace">The workspace.</param>
    public SettingsViewModel(IServerConnection connection, WorkspaceInfo workspace)
    {
        this.connection = connection;
        this.workspace = workspace;
        selectedService = Services.First(choice => choice.Service == workspace.Service);
        values = [];
        RefreshModelsCommand = new AsyncCommand(_ => Load());
        _ = Load();
    }

    private readonly IServerConnection connection;

    private WorkspaceInfo workspace;

    private ServiceChoice selectedService;

    private string? modelId;

    private Dictionary<string, string> values;

    private List<ModelInfo> offered = [];

    private IReadOnlyList<ModelChoice> models = [];

    private IReadOnlyList<ModelSettingViewModel> settings = [];

    /// <summary>
    /// Raised with the updated workspace after the server accepts a change.
    /// </summary>
    public event Action<WorkspaceInfo>? Changed;

    /// <summary>
    /// Reads the models of the chosen service and the model and settings of the workspace's AI session from the server again.
    /// </summary>
    /// <returns>A task that completes once the read has finished.</returns>
    public Task Reload() => Load();

    /// <summary>
    /// Gets the AI services to choose from.
    /// </summary>
    public IReadOnlyList<ServiceChoice> Services { get; } = [.. Enum.GetValues<AiService>().Select(service => new ServiceChoice(service, service.Label()))];

    /// <summary>
    /// Gets the command that reads the models of the chosen service from the server again.
    /// </summary>
    public AsyncCommand RefreshModelsCommand { get; }

    /// <summary>
    /// Gets the models the chosen service offers, the one the workspace uses included.
    /// </summary>
    public IReadOnlyList<ModelChoice> Models => models;

    /// <summary>
    /// Gets the settings the selected model offers.
    /// </summary>
    public IReadOnlyList<ModelSettingViewModel> Settings => settings;

    /// <summary>
    /// Gets a value indicating whether the selected model offers any setting.
    /// </summary>
    public bool HasSettings => settings.Count > 0;

    /// <summary>
    /// Gets what the service says about the selected model, or an empty text.
    /// </summary>
    public string ModelDescription => Selected()?.Description ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether the service says something about the selected model.
    /// </summary>
    public bool HasModelDescription => ModelDescription.Length > 0;

    /// <summary>
    /// Gets or sets the AI service; setting it switches to that service's default model and saves it to the server.
    /// </summary>
    public ServiceChoice SelectedService
    {
        get => selectedService;
        set
        {
            if (value is not null && value != selectedService)
            {
                _ = SelectService(value);
            }
        }
    }

    /// <summary>
    /// Gets or sets the model; setting it keeps the values of the settings both models have, gives the rest their defaults and saves it to the server.
    /// </summary>
    public ModelChoice? SelectedModel
    {
        get => models.FirstOrDefault(choice => choice.Id == modelId);
        set
        {
            if (value is not null && value.Id != modelId)
            {
                modelId = value.Id;
                values = Carry(Selected());
                BuildSettings();
                _ = Configure();
            }
        }
    }

    private ModelInfo? Selected() => offered.FirstOrDefault(model => model.Id == modelId);

    private Dictionary<string, string> Carry(ModelInfo? model) => model is null ? [] : model.Settings.ToDictionary(setting => setting.Id, setting => values.TryGetValue(setting.Id, out string? kept) && setting.Accepts(kept) ? kept : setting.Default);

    private void BuildSettings()
    {
        settings = [.. (Selected()?.Settings ?? []).Select(setting => new ModelSettingViewModel(setting, values.GetValueOrDefault(setting.Id, setting.Default), Change))];
        Raise(nameof(Settings));
        Raise(nameof(HasSettings));
        Raise(nameof(ModelDescription));
        Raise(nameof(HasModelDescription));
        Raise(nameof(SelectedModel));
    }

    private void Change(string id, string value)
    {
        values[id] = value;
        _ = Configure();
    }

    private async Task<IReadOnlyList<ModelInfo>?> List(AiService service)
    {
        IReadOnlyList<ModelInfo> found = [];
        return await Attempt(async () => found = await connection.ListModels(service, CancellationToken.None)) ? found : null;
    }

    private void Show(IReadOnlyList<ModelInfo> found)
    {
        offered = [.. found];
        List<ModelChoice> choices = [.. found.Select(model => new ModelChoice(model.Id, model.Name ?? model.Id))];

        if (modelId is not null && choices.All(choice => choice.Id != modelId))
        {
            choices.Insert(0, new ModelChoice(modelId, modelId));
        }

        models = choices;
        Raise(nameof(Models));
        BuildSettings();
    }

    private async Task Load()
    {
        if (await List(selectedService.Service) is not { } found)
        {
            return;
        }

        if (selectedService.Service == workspace.Service)
        {
            WorkspaceAi? ai = null;

            if (!await Attempt(async () => ai = await connection.GetWorkspaceAi(workspace.Id, CancellationToken.None)) || ai is null)
            {
                return;
            }

            modelId = ai.Model;
            values = new Dictionary<string, string>(ai.ModelSettings);
        }

        Show(found);
    }

    private async Task SelectService(ServiceChoice choice)
    {
        IReadOnlyList<ModelInfo>? found = await List(choice.Service);

        if (found is null)
        {
            Raise(nameof(SelectedService));
            return;
        }

        if (found.Count == 0)
        {
            Error = $"{choice.Name} offers no models. Is it installed and signed in on the server's machine?";
            Raise(nameof(SelectedService));
            return;
        }

        selectedService = choice;
        modelId = (found.FirstOrDefault(model => model.Id == choice.Service.DefaultModel()) ?? found[0]).Id;
        values = [];
        Show(found);
        values = Carry(Selected());
        BuildSettings();
        Raise(nameof(SelectedService));
        await Configure();
    }

    private async Task Configure()
    {
        WorkspaceInfo? updated = null;

        if (modelId is not null && await Attempt(async () => updated = await connection.ConfigureWorkspace(workspace.Id, selectedService.Service, modelId, values, CancellationToken.None)) && updated is not null)
        {
            workspace = updated;
            Changed?.Invoke(updated);
            return;
        }

        selectedService = Services.First(choice => choice.Service == workspace.Service);
        Raise(nameof(SelectedService));
        string? failure = Error;
        await Load();
        Error ??= failure;
    }
}
