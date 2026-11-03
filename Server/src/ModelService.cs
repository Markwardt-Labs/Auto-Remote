namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Lists the models each AI service offers with their settings, and checks a workspace's choice against them.
/// </summary>
internal interface IModelService
{
    /// <summary>
    /// Supplies the server settings the services are run with.
    /// </summary>
    /// <param name="config">The server settings.</param>
    void Configure(ServerConfig config);

    /// <summary>
    /// Lists the models a service can run with tools.
    /// </summary>
    /// <param name="service">The AI service.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The models; empty when the service is not installed, not signed in or has none.</returns>
    Task<IReadOnlyList<ModelInfo>> List(AiService service, CancellationToken cancellation);

    /// <summary>
    /// Checks that a service offers a model and that the model takes the values of its settings.
    /// </summary>
    /// <param name="service">The AI service.</param>
    /// <param name="model">The identifier of the model.</param>
    /// <param name="settings">The values of the model's settings by setting identifier.</param>
    /// <param name="cancellation">Cancels the check.</param>
    /// <exception cref="RequestException">The service offers no such model, or the model has no such setting or does not take the value.</exception>
    Task Validate(AiService service, string model, IReadOnlyDictionary<string, string> settings, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ModelService(Func<ServerConfig, AiService, IPromptProvider> createProvider) : IModelService
{
    private ServerConfig config = new() { Port = 0, Key = string.Empty };

    /// <inheritdoc />
    public void Configure(ServerConfig config) => this.config = config;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelInfo>> List(AiService service, CancellationToken cancellation) => [.. (await createProvider(config, service).GetModels(null, cancellation)).Where(model => model.CallsTools != false).Select(Describe)];

    /// <inheritdoc />
    public async Task Validate(AiService service, string model, IReadOnlyDictionary<string, string> settings, CancellationToken cancellation)
    {
        IReadOnlyList<ModelInfo> offered = await List(service, cancellation);

        if (offered.Count == 0)
        {
            throw new RequestException($"{service.Label()} offers no models. Is it installed and signed in?");
        }

        ModelInfo found = offered.FirstOrDefault(candidate => candidate.Id == model) ?? throw new RequestException($"{service.Label()} does not offer the model {model}.");

        foreach ((string id, string value) in settings)
        {
            ModelSettingInfo setting = found.Settings.FirstOrDefault(candidate => candidate.Id == id) ?? throw new RequestException($"{found.Name ?? found.Id} has no setting {id}.");

            if (!setting.Accepts(value))
            {
                throw new RequestException($"{found.Name ?? found.Id} does not take {value} for {setting.Name}.");
            }
        }
    }

    private ModelInfo Describe(PromptModel model) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Description = model.Description,
        AcceptsImages = model.AcceptsImages,
        ContextLimit = model.ContextLimit,
        Settings = [.. (model.Settings ?? []).Select(setting => new ModelSettingInfo
        {
            Id = setting.Id,
            Name = setting.Name,
            Description = setting.Description,
            Kind = setting.Kind switch { PromptModelSettingKind.Toggle => ModelSettingKind.Toggle, PromptModelSettingKind.Number => ModelSettingKind.Number, _ => ModelSettingKind.Choice },
            Choices = setting.Choices,
            Minimum = setting.Minimum,
            Maximum = setting.Maximum,
            Default = setting.Default
        })]
    };
}
