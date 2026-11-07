namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The environment settings of the server's AI: the default provider for new workspaces, where the tools are and the keys they sign in with, the models the local provider runs for each capability and the image generation service. The server keeps them and applies them to every workspace; API keys are only ever written, the server says whether one is set.
/// </summary>
internal sealed class EnvironmentViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="update">Looks for newer releases, which shows the section with the manual check, or <see langword="null" /> when this screen has no update system.</param>
    public EnvironmentViewModel(IServerConnection connection, UpdateViewModel? update = null)
    {
        this.connection = connection;
        Update = update;
        selectedDefault = Services[0];
        selectedImage = ImageKinds[0];
        CloseCommand = new AsyncCommand(async _ =>
        {
            if (await Apply())
            {
                Closed?.Invoke();
            }
        });
        RefreshLocalModelsCommand = new AsyncCommand(_ => RefreshLocal());
        RefreshImageModelsCommand = new AsyncCommand(_ => RefreshImage());
    }

    private readonly IServerConnection connection;

    private ServiceChoice selectedDefault;

    private Choice<ImageGenerationKind> selectedImage;

    private string claudeCommand = string.Empty;

    private string claudeConfigFolder = string.Empty;

    private string claudeApiKey = string.Empty;

    private bool hasClaudeApiKey;

    private bool clearClaudeApiKey;

    private string codexCommand = string.Empty;

    private string codexConfigFolder = string.Empty;

    private string codexApiKey = string.Empty;

    private bool hasCodexApiKey;

    private bool clearCodexApiKey;

    private string localCommand = string.Empty;

    private string localServer = string.Empty;

    private string openAiApiKey = string.Empty;

    private bool hasOpenAiApiKey;

    private bool clearOpenAiApiKey;

    private string localImageServer = string.Empty;

    private decimal? statusMinutes = 5;

    private string reviewPrompt = string.Empty;

    private string expandPrompt = string.Empty;

    private IReadOnlyList<string> localModelNames = [];

    private IReadOnlyList<string> imageModelNames = [];

    private string? notice;

    private WorkspaceDefaults defaults = new();

    private AiEnvironment? baseline;

    /// <summary>
    /// Raised when the user closes the screen.
    /// </summary>
    public event Action? Closed;

    /// <summary>
    /// Gets the providers to choose the default from.
    /// </summary>
    public IReadOnlyList<ServiceChoice> Services { get; } = [.. Enum.GetValues<AiService>().Select(service => new ServiceChoice(service, service.Label()))];

    /// <summary>
    /// Gets the image generation services to choose from.
    /// </summary>
    public IReadOnlyList<Choice<ImageGenerationKind>> ImageKinds { get; } =
    [
        new(ImageGenerationKind.None, "None"),
        new(ImageGenerationKind.OpenAi, "OpenAI"),
        new(ImageGenerationKind.Local, "Local Stable Diffusion server")
    ];

    /// <summary>
    /// Gets the capabilities to choose from for new workspaces.
    /// </summary>
    public IReadOnlyList<Choice<AiCapability>> Capabilities { get; } = [.. Enum.GetValues<AiCapability>().Select(value => new Choice<AiCapability>(value, value.ToString()))];

    /// <summary>
    /// Gets the efforts to choose from for new workspaces.
    /// </summary>
    public IReadOnlyList<Choice<AiEffort>> Efforts { get; } = [.. Enum.GetValues<AiEffort>().Select(value => new Choice<AiEffort>(value, value.ToString()))];

    /// <summary>
    /// Gets the rows for the model of the local provider for each capability.
    /// </summary>
    public IReadOnlyList<CapabilityRowViewModel> LocalRows { get; } = [.. Enum.GetValues<AiCapability>().Select(capability => new CapabilityRowViewModel(capability.ToString()))];

    /// <summary>
    /// Gets the rows for the checkpoint of the local image server for each capability.
    /// </summary>
    public IReadOnlyList<CapabilityRowViewModel> ImageRows { get; } = [.. Enum.GetValues<AiCapability>().Select(capability => new CapabilityRowViewModel(capability.ToString()))];

    /// <summary>
    /// Gets the command that closes the screen, which first sends what was changed to the server; the screen stays open, showing why, when the server refuses it.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Gets the command that asks the local model server which models it offers.
    /// </summary>
    public AsyncCommand RefreshLocalModelsCommand { get; }

    /// <summary>
    /// Gets the command that asks the image server which checkpoints it offers.
    /// </summary>
    public AsyncCommand RefreshImageModelsCommand { get; }

    /// <summary>
    /// Gets the models the local model server offered the last time they were refreshed.
    /// </summary>
    public IReadOnlyList<string> LocalModelNames => localModelNames;

    /// <summary>
    /// Gets the checkpoints the image server offered the last time they were refreshed.
    /// </summary>
    public IReadOnlyList<string> ImageModelNames => imageModelNames;

    /// <summary>
    /// Gets what the last action did, or <see langword="null" />.
    /// </summary>
    public string? Notice
    {
        get => notice;
        private set
        {
            if (Set(ref notice, value))
            {
                Raise(nameof(HasNotice));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether there is something to tell about the last action.
    /// </summary>
    public bool HasNotice => notice is not null;

    /// <summary>
    /// Gets or sets the provider new workspaces start with.
    /// </summary>
    public ServiceChoice SelectedDefault
    {
        get => selectedDefault;
        set
        {
            if (value is not null)
            {
                Set(ref selectedDefault, value);
            }
        }
    }

    /// <summary>
    /// Gets or sets the capability new workspaces start with.
    /// </summary>
    public Choice<AiCapability> NewCapability
    {
        get => Capabilities.First(choice => choice.Value == defaults.Capability);
        set
        {
            if (value is not null)
            {
                defaults = defaults with { Capability = value.Value };
                Raise(nameof(NewCapability));
            }
        }
    }

    /// <summary>
    /// Gets or sets the effort new workspaces start with.
    /// </summary>
    public Choice<AiEffort> NewEffort
    {
        get => Efforts.First(choice => choice.Value == defaults.Effort);
        set
        {
            if (value is not null)
            {
                defaults = defaults with { Effort = value.Value };
                Raise(nameof(NewEffort));
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the AI of new workspaces may run shell commands.
    /// </summary>
    public bool NewIsShellAllowed
    {
        get => defaults.IsShellAllowed;
        set
        {
            defaults = defaults with { IsShellAllowed = value };
            Raise(nameof(NewIsShellAllowed));
        }
    }

    /// <summary>
    /// Gets or sets whether the AI of new workspaces may use files outside the workspace.
    /// </summary>
    public bool NewIsSystemAccessAllowed
    {
        get => defaults.IsSystemAccessAllowed;
        set
        {
            defaults = defaults with { IsSystemAccessAllowed = value };
            Raise(nameof(NewIsSystemAccessAllowed));
        }
    }

    /// <summary>
    /// Gets or sets whether the AI of new workspaces may use the web.
    /// </summary>
    public bool NewIsWebAllowed
    {
        get => defaults.IsWebAllowed;
        set
        {
            defaults = defaults with { IsWebAllowed = value };
            Raise(nameof(NewIsWebAllowed));
        }
    }

    /// <summary>
    /// Gets or sets whether the AI of new workspaces may generate images, which needs an image generation service below.
    /// </summary>
    public bool NewIsImageGenerationAllowed
    {
        get => defaults.IsImageGenerationAllowed;
        set
        {
            defaults = defaults with { IsImageGenerationAllowed = value };
            Raise(nameof(NewIsImageGenerationAllowed));
        }
    }

    /// <summary>
    /// Gets or sets the standing instructions of new workspaces; empty for none.
    /// </summary>
    public string NewInstructions
    {
        get => defaults.Instructions;
        set
        {
            defaults = defaults with { Instructions = value ?? string.Empty };
            Raise(nameof(NewInstructions));
        }
    }

    /// <summary>
    /// Gets or sets the Claude Code executable.
    /// </summary>
    public string ClaudeCommand
    {
        get => claudeCommand;
        set => Set(ref claudeCommand, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the folder Claude Code keeps its configuration in; empty for its own default.
    /// </summary>
    public string ClaudeConfigFolder
    {
        get => claudeConfigFolder;
        set => Set(ref claudeConfigFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets a new API key for Claude Code; empty keeps the key as it is.
    /// </summary>
    public string ClaudeApiKey
    {
        get => claudeApiKey;
        set => Set(ref claudeApiKey, value ?? string.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether the server holds an API key for Claude Code.
    /// </summary>
    public bool HasClaudeApiKey => hasClaudeApiKey;

    /// <summary>
    /// Gets or sets whether the server's Claude Code key is removed when saving.
    /// </summary>
    public bool ClearClaudeApiKey
    {
        get => clearClaudeApiKey;
        set => Set(ref clearClaudeApiKey, value);
    }

    /// <summary>
    /// Gets or sets the Codex executable.
    /// </summary>
    public string CodexCommand
    {
        get => codexCommand;
        set => Set(ref codexCommand, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the folder Codex keeps its configuration in; empty for its own default.
    /// </summary>
    public string CodexConfigFolder
    {
        get => codexConfigFolder;
        set => Set(ref codexConfigFolder, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets a new API key for Codex; empty keeps the key as it is.
    /// </summary>
    public string CodexApiKey
    {
        get => codexApiKey;
        set => Set(ref codexApiKey, value ?? string.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether the server holds an API key for Codex.
    /// </summary>
    public bool HasCodexApiKey => hasCodexApiKey;

    /// <summary>
    /// Gets or sets whether the server's Codex key is removed when saving.
    /// </summary>
    public bool ClearCodexApiKey
    {
        get => clearCodexApiKey;
        set => Set(ref clearCodexApiKey, value);
    }

    /// <summary>
    /// Gets or sets the OpenCode executable that runs the local models.
    /// </summary>
    public string LocalCommand
    {
        get => localCommand;
        set => Set(ref localCommand, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the address of the OpenAI compatible server that runs the local models.
    /// </summary>
    public string LocalServer
    {
        get => localServer;
        set => Set(ref localServer, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the image generation service.
    /// </summary>
    public Choice<ImageGenerationKind> SelectedImage
    {
        get => selectedImage;
        set
        {
            if (value is not null && Set(ref selectedImage, value))
            {
                Raise(nameof(UsesOpenAi));
                Raise(nameof(UsesLocalImages));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether images are generated by OpenAI.
    /// </summary>
    public bool UsesOpenAi => selectedImage.Value == ImageGenerationKind.OpenAi;

    /// <summary>
    /// Gets a value indicating whether images are generated by a local Stable Diffusion server.
    /// </summary>
    public bool UsesLocalImages => selectedImage.Value == ImageGenerationKind.Local;

    /// <summary>
    /// Gets or sets a new OpenAI API key for image generation; empty keeps the key as it is.
    /// </summary>
    public string OpenAiApiKey
    {
        get => openAiApiKey;
        set => Set(ref openAiApiKey, value ?? string.Empty);
    }

    /// <summary>
    /// Gets a value indicating whether the server holds an OpenAI API key.
    /// </summary>
    public bool HasOpenAiApiKey => hasOpenAiApiKey;

    /// <summary>
    /// Gets or sets whether the server's OpenAI key is removed when saving.
    /// </summary>
    public bool ClearOpenAiApiKey
    {
        get => clearOpenAiApiKey;
        set => Set(ref clearOpenAiApiKey, value);
    }

    /// <summary>
    /// Gets or sets the address of the local Stable Diffusion server.
    /// </summary>
    public string LocalImageServer
    {
        get => localImageServer;
        set => Set(ref localImageServer, value ?? string.Empty);
    }

    /// <summary>
    /// Gets the update system whose manual check this screen offers, or <see langword="null" /> when there is none.
    /// </summary>
    public UpdateViewModel? Update { get; }

    /// <summary>
    /// Gets a value indicating whether this screen offers the manual update check.
    /// </summary>
    public bool HasUpdate => Update is not null;

    /// <summary>
    /// Gets or sets how often, in minutes, the AI is told to report its status in every workspace that has no interval of its own; empty restores the default of 5.
    /// </summary>
    public decimal? StatusMinutes
    {
        get => statusMinutes;
        set => Set(ref statusMinutes, value);
    }

    /// <summary>
    /// Gets or sets the prompt the review button sends in every workspace that has none of its own; empty restores the default.
    /// </summary>
    public string ReviewPrompt
    {
        get => reviewPrompt;
        set => Set(ref reviewPrompt, value ?? string.Empty);
    }

    /// <summary>
    /// Gets or sets the prompt the expand button sends in every workspace that has none of its own; empty restores the default.
    /// </summary>
    public string ExpandPrompt
    {
        get => expandPrompt;
        set => Set(ref expandPrompt, value ?? string.Empty);
    }

    /// <summary>
    /// Reads the environment from the server.
    /// </summary>
    /// <returns>A task that completes once the read has finished.</returns>
    public async Task Load()
    {
        AiEnvironment? read = null;

        if (await Attempt(async () => read = await connection.GetAiEnvironment(CancellationToken.None)) && read is not null)
        {
            Show(read);
        }
    }

    private string? KeyUpdate(string entered, bool clear) => clear ? string.Empty : entered.Trim().Length > 0 ? entered.Trim() : null;

    private string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private CapabilityNames Names(IReadOnlyList<CapabilityRowViewModel> rows) => new() { Fast = rows[0].Model.Trim(), Balanced = rows[1].Model.Trim(), Strong = rows[2].Model.Trim(), Max = rows[3].Model.Trim() };

    private int? Tokens(CapabilityRowViewModel row) => row.Context is { } value ? (int)value : null;

    private AiEnvironment Build()
    {
        CapabilityContexts? contexts = LocalRows.Any(row => row.Context is not null) ? new() { Fast = Tokens(LocalRows[0]), Balanced = Tokens(LocalRows[1]), Strong = Tokens(LocalRows[2]), Max = Tokens(LocalRows[3]) } : null;
        return new AiEnvironment
        {
            DefaultService = selectedDefault.Service,
            ClaudeCommand = claudeCommand.Trim(),
            ClaudeConfigFolder = Blank(claudeConfigFolder),
            ClaudeApiKey = KeyUpdate(claudeApiKey, clearClaudeApiKey),
            CodexCommand = codexCommand.Trim(),
            CodexConfigFolder = Blank(codexConfigFolder),
            CodexApiKey = KeyUpdate(codexApiKey, clearCodexApiKey),
            LocalCommand = localCommand.Trim(),
            LocalServer = localServer.Trim(),
            LocalModels = Names(LocalRows),
            LocalContexts = contexts,
            ImageGeneration = selectedImage.Value,
            OpenAiApiKey = KeyUpdate(openAiApiKey, clearOpenAiApiKey),
            LocalImageServer = localImageServer.Trim(),
            LocalImageModels = Names(ImageRows),
            NewWorkspaces = defaults with { Instructions = defaults.Instructions.Trim() },
            StatusMinutes = statusMinutes is { } minutes ? (int)Math.Clamp(Math.Round(minutes), 1, 1440) : 5,
            ReviewPrompt = reviewPrompt.Trim(),
            ExpandPrompt = expandPrompt.Trim()
        };
    }

    private void Show(AiEnvironment environment)
    {
        selectedDefault = Services.First(choice => choice.Service == environment.DefaultService);
        selectedImage = ImageKinds.First(choice => choice.Value == environment.ImageGeneration);
        claudeCommand = environment.ClaudeCommand;
        claudeConfigFolder = environment.ClaudeConfigFolder ?? string.Empty;
        claudeApiKey = string.Empty;
        hasClaudeApiKey = environment.HasClaudeApiKey;
        clearClaudeApiKey = false;
        codexCommand = environment.CodexCommand;
        codexConfigFolder = environment.CodexConfigFolder ?? string.Empty;
        codexApiKey = string.Empty;
        hasCodexApiKey = environment.HasCodexApiKey;
        clearCodexApiKey = false;
        localCommand = environment.LocalCommand;
        localServer = environment.LocalServer;
        openAiApiKey = string.Empty;
        hasOpenAiApiKey = environment.HasOpenAiApiKey;
        clearOpenAiApiKey = false;
        localImageServer = environment.LocalImageServer;
        statusMinutes = environment.StatusMinutes;
        reviewPrompt = environment.ReviewPrompt;
        expandPrompt = environment.ExpandPrompt;
        defaults = environment.NewWorkspaces;
        string[] local = [environment.LocalModels.Fast, environment.LocalModels.Balanced, environment.LocalModels.Strong, environment.LocalModels.Max];
        int?[] contexts = [environment.LocalContexts?.Fast, environment.LocalContexts?.Balanced, environment.LocalContexts?.Strong, environment.LocalContexts?.Max];
        string[] images = [environment.LocalImageModels.Fast, environment.LocalImageModels.Balanced, environment.LocalImageModels.Strong, environment.LocalImageModels.Max];

        for (int index = 0; index < LocalRows.Count; index++)
        {
            LocalRows[index].Model = local[index];
            LocalRows[index].Context = contexts[index];
            ImageRows[index].Model = images[index];
        }

        baseline = Build();
        Raise(null);
    }

    private async Task<bool> Apply()
    {
        AiEnvironment wanted = Build();
        Notice = null;

        if (baseline is null || wanted == baseline)
        {
            return true;
        }

        AiEnvironment? saved = null;

        if (await Attempt(async () => saved = await connection.SetAiEnvironment(wanted, CancellationToken.None)) && saved is not null)
        {
            Show(saved);
            return true;
        }

        return false;
    }

    private async Task RefreshLocal()
    {
        IReadOnlyList<string> found = [];
        Notice = null;

        if (await Attempt(async () => found = await connection.ListLocalModels(Blank(localServer), CancellationToken.None)))
        {
            localModelNames = found;
            Raise(nameof(LocalModelNames));
            Notice = found.Count == 0 ? "The server offers no models." : $"Found {found.Count} model{(found.Count == 1 ? string.Empty : "s")}.";
        }
    }

    private async Task RefreshImage()
    {
        IReadOnlyList<string> found = [];
        Notice = null;

        if (await Attempt(async () => found = await connection.ListImageModels(Blank(localImageServer), CancellationToken.None)))
        {
            imageModelNames = found;
            Raise(nameof(ImageModelNames));
            Notice = found.Count == 0 ? "The server offers no checkpoints." : $"Found {found.Count} checkpoint{(found.Count == 1 ? string.Empty : "s")}.";
        }
    }
}
