namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Makes the Live AI provider of each AI service, over the executables and data folder of the server settings.
/// </summary>
internal interface IAiProviders
{
    /// <summary>
    /// Gets the provider of an AI service. The same provider answers for as long as the settings are the same, so what it has learned of the models stays.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="service">The AI service.</param>
    /// <returns>A provider that stores its sessions in the server's data folder.</returns>
    IPromptProvider Create(ServerConfig config, AiService service);
}

/// <inheritdoc />
internal sealed class AiProviders : IAiProviders
{
    private readonly ConcurrentDictionary<AiService, (ServerConfig Config, IPromptProvider Provider)> made = new();

    /// <inheritdoc />
    public IPromptProvider Create(ServerConfig config, AiService service)
    {
        (ServerConfig Config, IPromptProvider Provider) known = made.GetOrAdd(service, _ => (config, Make(config, service)));

        if (known.Config == config)
        {
            return known.Provider;
        }

        made[service] = (config, Make(config, service));
        return made[service].Provider;
    }

    private IPromptProvider Make(ServerConfig config, AiService service) => new PromptProvider(Engine(config, service), Path.Combine(config.DataFolder, "liveai"));

    private IPromptEngine Engine(ServerConfig config, AiService service) => service switch
    {
        AiService.Codex => new CodexEngine(config.CodexCommand),
        AiService.OpenCode => new OpenCodeEngine(config.OpenCodeCommand, [config.LocalModelUrl]),
        _ => new ClaudeEngine(config.ClaudeCommand)
    };
}
