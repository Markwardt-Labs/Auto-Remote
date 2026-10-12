namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The settings of one workspace: the AI service, model and effort its prompts use. The models and efforts offered follow the chosen service.
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
        model = workspace.Model;
        effort = workspace.Effort;
    }

    private readonly IServerConnection connection;

    private WorkspaceInfo workspace;

    private ServiceChoice selectedService;

    private AiModel model;

    private AiEffort effort;

    /// <summary>
    /// Raised with the updated workspace after the server accepts a change.
    /// </summary>
    public event Action<WorkspaceInfo>? Changed;

    /// <summary>
    /// Gets the AI services to choose from.
    /// </summary>
    public IReadOnlyList<ServiceChoice> Services { get; } = [.. Enum.GetValues<AiService>().Select(service => new ServiceChoice(service, service.Label()))];

    /// <summary>
    /// Gets the models the chosen service offers.
    /// </summary>
    public IReadOnlyList<AiModel> Models => selectedService.Service.Models();

    /// <summary>
    /// Gets the efforts the chosen service offers.
    /// </summary>
    public IReadOnlyList<AiEffort> Efforts => selectedService.Service.Efforts();

    /// <summary>
    /// Gets or sets the AI service; setting it switches to that service's default model and effort and saves them to the server.
    /// </summary>
    public ServiceChoice SelectedService
    {
        get => selectedService;
        set
        {
            if (value is not null && Set(ref selectedService, value))
            {
                model = value.Service.Models()[0];
                effort = value.Service.DefaultEffort();
                RaiseChoices();
                _ = Configure();
            }
        }
    }

    /// <summary>
    /// Gets or sets the model; setting it saves it to the server.
    /// </summary>
    public AiModel Model
    {
        get => model;
        set
        {
            if (Set(ref model, value))
            {
                _ = Configure();
            }
        }
    }

    /// <summary>
    /// Gets or sets the effort; setting it saves it to the server.
    /// </summary>
    public AiEffort Effort
    {
        get => effort;
        set
        {
            if (Set(ref effort, value))
            {
                _ = Configure();
            }
        }
    }

    private async Task Configure()
    {
        WorkspaceInfo? updated = null;

        if (await Attempt(async () => updated = await connection.ConfigureWorkspace(workspace.Id, selectedService.Service, model, effort, CancellationToken.None)) && updated is not null)
        {
            workspace = updated;
            Changed?.Invoke(updated);
            return;
        }

        selectedService = Services.First(choice => choice.Service == workspace.Service);
        model = workspace.Model;
        effort = workspace.Effort;
        RaiseChoices();
    }

    private void RaiseChoices()
    {
        Raise(nameof(SelectedService));
        Raise(nameof(Models));
        Raise(nameof(Efforts));
        Raise(nameof(Model));
        Raise(nameof(Effort));
    }
}
