namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Picks the AI provider a new workspace runs on for good and, for Claude Code and Codex, an optional API key used for just that workspace's session. The provider starts as the default of the server's environment.
/// </summary>
internal sealed class ProviderPickerViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderPickerViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    public ProviderPickerViewModel(IServerConnection connection)
    {
        this.connection = connection;
        selected = Services[0];
    }

    private readonly IServerConnection connection;

    private ServiceChoice selected;

    private string apiKey = string.Empty;

    /// <summary>
    /// Gets the providers to choose from.
    /// </summary>
    public IReadOnlyList<ServiceChoice> Services { get; } = [.. Enum.GetValues<AiService>().Select(service => new ServiceChoice(service, service.Label()))];

    /// <summary>
    /// Gets or sets the provider.
    /// </summary>
    public ServiceChoice SelectedService
    {
        get => selected;
        set
        {
            if (value is not null && Set(ref selected, value))
            {
                Raise(nameof(TakesApiKey));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the chosen provider signs in with an API key.
    /// </summary>
    public bool TakesApiKey => selected.Service.TakesApiKey();

    /// <summary>
    /// Gets or sets the API key for the workspace's session; empty signs in the way the tool is signed in on the server.
    /// </summary>
    public string ApiKey
    {
        get => apiKey;
        set => Set(ref apiKey, value);
    }

    /// <summary>
    /// Gets the chosen provider.
    /// </summary>
    public AiService Service => selected.Service;

    /// <summary>
    /// Gets the API key to give the server, or <see langword="null" /> when the provider takes none or none is entered.
    /// </summary>
    public string? Key => TakesApiKey && !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : null;

    /// <summary>
    /// Selects the default provider of the server's environment.
    /// </summary>
    /// <returns>A task that completes once the environment is read; a failure leaves the first provider selected.</returns>
    public async Task Load()
    {
        AiEnvironment? environment = null;

        if (await Attempt(async () => environment = await connection.GetAiEnvironment(CancellationToken.None)) && environment is not null)
        {
            SelectedService = Services.First(choice => choice.Service == environment.DefaultService);
        }
    }
}
