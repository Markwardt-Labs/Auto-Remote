namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Owns the one Live AI prompter of the server and the environment it runs in, which the user sets from the clients and the server keeps in its data folder, replacing what the server config used to say about the AI.
/// </summary>
internal interface IPrompterHost
{
    /// <summary>
    /// Gets the prompter every session of the server is made by.
    /// </summary>
    /// <exception cref="InvalidOperationException">The host was not configured yet.</exception>
    IPrompter Prompter { get; }

    /// <summary>
    /// Gets the provider that new workspaces start with.
    /// </summary>
    AiService DefaultService { get; }

    /// <summary>
    /// Takes over the AI settings an older release kept in the server config when there is no stored environment yet, makes the prompter over the sessions in the data folder (Live AI upgrades the ones an earlier version stored itself) and applies the environment the server kept, which also holds what every new session starts with.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="cancellation">Cancels the read of the stored environment.</param>
    Task Configure(ServerConfig config, CancellationToken cancellation);

    /// <summary>
    /// Reads the environment, without the API keys.
    /// </summary>
    /// <returns>The environment.</returns>
    AiEnvironment Get();

    /// <summary>
    /// Replaces the environment, keeps it and applies it to the prompter.
    /// </summary>
    /// <param name="environment">The new environment; an API key that is <see langword="null" /> stays as it is and an empty one removes it.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <returns>The environment as it now is, without the API keys.</returns>
    /// <exception cref="RequestException">The environment is not valid.</exception>
    Task<AiEnvironment> Set(AiEnvironment environment, CancellationToken cancellation);

    /// <summary>
    /// Puts the instructions every AI gets in front of the user's own, which is what a session is configured with.
    /// </summary>
    /// <param name="user">The user's own instructions, or <see langword="null" /> for none.</param>
    /// <returns>The instructions for the session.</returns>
    string ComposeInstructions(string? user);

    /// <summary>
    /// Takes the instructions every AI gets off what a session is configured with, leaving what the user wrote.
    /// </summary>
    /// <param name="stored">The instructions of the session.</param>
    /// <returns>The user's own instructions, or <see langword="null" /> when there are none.</returns>
    string? UserInstructions(string? stored);

    /// <summary>
    /// Makes the AI session of a new workspace: in the folder, on the provider, on an API key of its own when one is given (which Live AI stores with the session; the server keeps no copy), balanced and thinking hard, with the shell, the web and, when the environment has an image service, image generation on, no sandbox, and the built-in instructions that keep the AI working in the foreground in front of the ones of the environment. The session is closed again; it is loaded when it is first used.
    /// </summary>
    /// <param name="folder">The folder of the workspace.</param>
    /// <param name="service">The provider.</param>
    /// <param name="apiKey">The API key of the session, or <see langword="null" /> for the key of the environment.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <returns>The identifier of the session.</returns>
    /// <exception cref="RequestException">The provider takes no API key, the folder does not exist, or the session cannot be made (local models without a model for every capability, for one).</exception>
    Task<string> CreateSession(string folder, AiService service, string? apiKey, CancellationToken cancellation);

    /// <summary>
    /// Tells Live AI that a session's workspace is in another folder now, which it tracks without loading the session, so it works when the old folder is gone. Nothing on disk is moved. The session must not be open.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="folder">The existing folder that is the workspace now.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <exception cref="RequestException">The folder does not exist, or the session cannot be moved.</exception>
    Task MoveSession(string sessionId, string folder, CancellationToken cancellation);

    /// <summary>
    /// Gives a session an earlier version of Live AI stored what the server ran every session with before sessions kept their own settings, which that version never stored, so that the workspace keeps working as it did: the shell, the web and the files outside the workspace allowed, image generation when the environment has an image service, and instructions to work in the foreground. These do not follow the defaults of new workspaces, which are for workspaces that did not exist yet. The session must not be open.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellation">Cancels the work.</param>
    /// <exception cref="RequestException">The session cannot be loaded.</exception>
    Task Adopt(string sessionId, CancellationToken cancellation);

    /// <summary>
    /// Makes the setup of a provider.
    /// </summary>
    /// <param name="service">The provider.</param>
    /// <param name="apiKey">The API key of the session, or <see langword="null" /> for the key of the environment.</param>
    /// <returns>The setup, with nothing else chosen.</returns>
    /// <exception cref="RequestException">An API key was given for a provider that takes none.</exception>
    PromptSetup Setup(AiService service, string? apiKey = null);

    /// <summary>
    /// Asks the model server which models it offers.
    /// </summary>
    /// <param name="server">The address of the server, or <see langword="null" /> for the one of the environment.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The names of the models.</returns>
    /// <exception cref="RequestException">The address is not valid or the server could not be asked.</exception>
    Task<IReadOnlyList<string>> ListLocalModels(string? server, CancellationToken cancellation);

    /// <summary>
    /// Asks the Stable Diffusion server which checkpoints it offers.
    /// </summary>
    /// <param name="server">The address of the server, or <see langword="null" /> for the one of the environment.</param>
    /// <param name="cancellation">Cancels the call.</param>
    /// <returns>The names of the checkpoints.</returns>
    /// <exception cref="RequestException">The address is not valid or the server could not be asked.</exception>
    Task<IReadOnlyList<string>> ListImageModels(string? server, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class PrompterHost(Func<string, IPrompter>? createPrompter = null, Func<Uri?, CancellationToken, Task<IReadOnlyList<string>>>? listLocalModels = null, Func<Uri?, CancellationToken, Task<IReadOnlyList<string>>>? listImageModels = null) : IPrompterHost
{
    private readonly Func<string, IPrompter> create = createPrompter ?? (folder => new Prompter(folder));

    private readonly Func<Uri?, CancellationToken, Task<IReadOnlyList<string>>> localModels = listLocalModels ?? ((server, cancellation) => LocalSetup.ListModels(server, cancellation));

    private readonly Func<Uri?, CancellationToken, Task<IReadOnlyList<string>>> imageModels = listImageModels ?? ((server, cancellation) => LocalImageSetup.ListModels(server, cancellation));

    private readonly SemaphoreSlim writing = new(1, 1);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IBuiltInInstructions builtIn = new BuiltInInstructions();

    private readonly IStoredFile stored = new StoredFile([], true);

    private IPrompter? prompter;

    private string? file;

    private readonly AiEnvironment defaults = new();

    private AiEnvironment kept = new();

    /// <inheritdoc />
    public IPrompter Prompter => prompter ?? throw new InvalidOperationException("The AI is not set up yet.");

    /// <inheritdoc />
    public AiService DefaultService => kept.DefaultService;

    /// <inheritdoc />
    public async Task Configure(ServerConfig config, CancellationToken cancellation)
    {
        file = Path.Combine(config.DataFolder, "environment.json");
        bool stored = File.Exists(file);
        kept = await Read(cancellation);

        if (!stored && Imported(config) is { } imported)
        {
            kept = imported;
            await Write(kept, cancellation);
        }

        string folder = Path.Combine(config.DataFolder, "liveai");
        prompter = create(folder);
        Apply(kept);
    }

    /// <inheritdoc />
    public string ComposeInstructions(string? user) => builtIn.Compose(user);

    /// <inheritdoc />
    public string? UserInstructions(string? stored) => builtIn.Visible(stored);

    /// <inheritdoc />
    public AiEnvironment Get() => kept with { ClaudeApiKey = null, CodexApiKey = null, OpenAiApiKey = null, HasClaudeApiKey = !string.IsNullOrEmpty(kept.ClaudeApiKey), HasCodexApiKey = !string.IsNullOrEmpty(kept.CodexApiKey), HasOpenAiApiKey = !string.IsNullOrEmpty(kept.OpenAiApiKey) };

    /// <inheritdoc />
    public async Task<AiEnvironment> Set(AiEnvironment environment, CancellationToken cancellation)
    {
        await writing.WaitAsync(cancellation);

        try
        {
            AiEnvironment given = environment with
            {
                ClaudeCommand = environment.ClaudeCommand ?? string.Empty,
                CodexCommand = environment.CodexCommand ?? string.Empty,
                LocalCommand = environment.LocalCommand ?? string.Empty,
                LocalServer = environment.LocalServer ?? string.Empty,
                LocalImageServer = environment.LocalImageServer ?? string.Empty,
                LocalModels = environment.LocalModels ?? new CapabilityNames(),
                LocalImageModels = environment.LocalImageModels ?? new CapabilityNames(),
                NewWorkspaces = environment.NewWorkspaces ?? new WorkspaceDefaults()
            };
            AiEnvironment next = given with
            {
                ClaudeApiKey = Merge(environment.ClaudeApiKey, kept.ClaudeApiKey),
                CodexApiKey = Merge(environment.CodexApiKey, kept.CodexApiKey),
                OpenAiApiKey = Merge(environment.OpenAiApiKey, kept.OpenAiApiKey),
                ClaudeConfigFolder = Blank(environment.ClaudeConfigFolder),
                CodexConfigFolder = Blank(environment.CodexConfigFolder),
                NewWorkspaces = given.NewWorkspaces with { Instructions = (given.NewWorkspaces.Instructions ?? string.Empty).Trim() },
                ReviewPrompt = string.IsNullOrWhiteSpace(environment.ReviewPrompt) ? defaults.ReviewPrompt : environment.ReviewPrompt.Trim(),
                ExpandPrompt = string.IsNullOrWhiteSpace(environment.ExpandPrompt) ? defaults.ExpandPrompt : environment.ExpandPrompt.Trim(),
                ClaudeCommand = given.ClaudeCommand.Trim(),
                CodexCommand = given.CodexCommand.Trim(),
                LocalCommand = given.LocalCommand.Trim(),
                LocalServer = given.LocalServer.Trim(),
                LocalImageServer = given.LocalImageServer.Trim()
            };
            Check(next);
            Apply(next);
            kept = next;
            await Write(next, cancellation);
            return Get();
        }
        finally
        {
            writing.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateSession(string folder, AiService service, string? apiKey, CancellationToken cancellation)
    {
        PromptSetup setup = Setup(service, apiKey) with { WorkspacePath = folder };

        try
        {
            await using IPromptSession session = await Prompter.Create(setup, cancellation);
            return session.Id;
        }
        catch (Exception exception) when (exception is PromptSessionException or DirectoryNotFoundException or ArgumentException)
        {
            throw new RequestException(exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task MoveSession(string sessionId, string folder, CancellationToken cancellation)
    {
        try
        {
            await Prompter.Move(sessionId, folder, cancellation);
        }
        catch (Exception exception) when (exception is PromptSessionException or DirectoryNotFoundException or ArgumentException)
        {
            throw new RequestException(exception.Message);
        }
    }

    /// <inheritdoc />
    public async Task Adopt(string sessionId, CancellationToken cancellation)
    {
        try
        {
            await using IPromptSession session = await Prompter.Load(sessionId, cancellation);
            IPromptSessionConfiguration configuration = session.Configuration;
            configuration.IsShellAllowed = true;
            configuration.IsWebAllowed = true;
            configuration.IsSystemAccessAllowed = true;
            configuration.IsImageGenerationAllowed = kept.ImageGeneration != ImageGenerationKind.None;
            configuration.Instructions = builtIn.Compose(null);
        }
        catch (Exception exception) when (exception is PromptSessionException or ArgumentException or DirectoryNotFoundException or IOException)
        {
            throw new RequestException(exception.Message);
        }
    }

    /// <inheritdoc />
    public PromptSetup Setup(AiService service, string? apiKey = null)
    {
        string? key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();

        if (key is not null && !service.TakesApiKey())
        {
            throw new RequestException($"{service.Label()} takes no API key.");
        }

        return service switch
        {
            AiService.Codex => new CodexSetup { ApiKey = key },
            AiService.Local => new LocalSetup(),
            _ => new ClaudeSetup { ApiKey = key }
        };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListLocalModels(string? server, CancellationToken cancellation) => List(localModels, server ?? kept.LocalServer, cancellation);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListImageModels(string? server, CancellationToken cancellation) => List(imageModels, server ?? kept.LocalImageServer, cancellation);

    private async Task<IReadOnlyList<string>> List(Func<Uri?, CancellationToken, Task<IReadOnlyList<string>>> list, string server, CancellationToken cancellation)
    {
        Uri address = Address(server, "The address of the server");

        try
        {
            return await list(address, cancellation);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or PromptSessionException or JsonException or InvalidOperationException || exception is TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            throw new RequestException($"The server at {address} could not be asked for its models: {exception.Message}");
        }
    }

    private string? Merge(string? given, string? current) => given is null ? current : given.Length == 0 ? null : given.Trim();

    private string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private Uri Address(string text, string what)
        => Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https" ? address : throw new RequestException($"{what} must be an http or https address.");

    private void Check(AiEnvironment environment)
    {
        if (!Enum.IsDefined(environment.NewWorkspaces.Capability) || !Enum.IsDefined(environment.NewWorkspaces.Effort))
        {
            throw new RequestException("The capability or the effort of new workspaces is not one the AI knows.");
        }

        if (!Enum.IsDefined(environment.DefaultService) || !Enum.IsDefined(environment.ImageGeneration))
        {
            throw new RequestException("The provider or the image generation service is not one the server knows.");
        }

        foreach ((string command, string what) in new[] { (environment.ClaudeCommand, "Claude Code"), (environment.CodexCommand, "Codex"), (environment.LocalCommand, "OpenCode") })
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new RequestException($"The command of {what} is empty.");
            }
        }

        Address(environment.LocalServer, "The address of the model server");

        if (environment.LocalContexts is { } contexts && new[] { contexts.Fast, contexts.Balanced, contexts.Strong, contexts.Max }.Any(size => size is <= 0))
        {
            throw new RequestException("A context size must be more than zero.");
        }

        if (Partial(environment.LocalModels))
        {
            throw new RequestException("Local models need a model for every capability, or none at all.");
        }

        if (environment.ImageGeneration == ImageGenerationKind.OpenAi && string.IsNullOrEmpty(environment.OpenAiApiKey))
        {
            throw new RequestException("Image generation with OpenAI needs an API key.");
        }

        if (environment.ImageGeneration == ImageGenerationKind.Local)
        {
            Address(environment.LocalImageServer, "The address of the image server");

            if (Blanks(environment.LocalImageModels))
            {
                throw new RequestException("Local image generation needs a checkpoint for every capability.");
            }
        }
    }

    private bool Partial(CapabilityNames names) => Blanks(names) && new[] { names.Fast, names.Balanced, names.Strong, names.Max }.Any(name => !string.IsNullOrWhiteSpace(name));

    private bool Blanks(CapabilityNames names) => new[] { names.Fast, names.Balanced, names.Strong, names.Max }.Any(string.IsNullOrWhiteSpace);

    private void Apply(AiEnvironment environment)
    {
        PromptCapabilityModels? models = Blanks(environment.LocalModels) ? null : new PromptCapabilityModels { Fast = environment.LocalModels.Fast.Trim(), Balanced = environment.LocalModels.Balanced.Trim(), Strong = environment.LocalModels.Strong.Trim(), Max = environment.LocalModels.Max.Trim() };
        PromptCapabilityContexts? contexts = environment.LocalContexts is { } sizes ? new PromptCapabilityContexts { Fast = sizes.Fast, Balanced = sizes.Balanced, Strong = sizes.Strong, Max = sizes.Max } : null;
        PromptImageSetup? images = environment.ImageGeneration switch
        {
            ImageGenerationKind.OpenAi => new OpenAiImageSetup { ApiKey = environment.OpenAiApiKey! },
            ImageGenerationKind.Local => new LocalImageSetup { Server = Address(environment.LocalImageServer, "The address of the image server"), Models = new PromptCapabilityModels { Fast = environment.LocalImageModels.Fast.Trim(), Balanced = environment.LocalImageModels.Balanced.Trim(), Strong = environment.LocalImageModels.Strong.Trim(), Max = environment.LocalImageModels.Max.Trim() } },
            _ => null
        };
        Prompter.Environment = new PromptEnvironment
        {
            ClaudeCommand = environment.ClaudeCommand.Trim(),
            ClaudeConfigFolder = environment.ClaudeConfigFolder,
            ClaudeApiKey = environment.ClaudeApiKey,
            CodexCommand = environment.CodexCommand.Trim(),
            CodexConfigFolder = environment.CodexConfigFolder,
            CodexApiKey = environment.CodexApiKey,
            LocalCommand = environment.LocalCommand.Trim(),
            LocalSettings = new PromptServerSettings { Server = Address(environment.LocalServer, "The address of the model server"), Models = models, Contexts = contexts },
            ImageGeneration = images,
            Capability = Enum.Parse<PromptCapability>(environment.NewWorkspaces.Capability.ToString()),
            Effort = Enum.Parse<PromptEffort>(environment.NewWorkspaces.Effort.ToString()),
            IsShellAllowed = environment.NewWorkspaces.IsShellAllowed,
            IsSystemAccessAllowed = environment.NewWorkspaces.IsSystemAccessAllowed,
            IsWebAllowed = environment.NewWorkspaces.IsWebAllowed,
            IsImageGenerationAllowed = environment.NewWorkspaces.IsImageGenerationAllowed,
            Instructions = builtIn.Compose(environment.NewWorkspaces.Instructions)
        };
    }

    private AiEnvironment? Imported(ServerConfig config)
    {
        string? Text(string name) => config.Other?.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()!.Trim() : null;

        string? claude = Text("claudeCommand");
        string? codex = Text("codexCommand");
        string? local = Text("openCodeCommand");
        string? server = Text("localModelUrl");
        bool valid = server is not null && Uri.TryCreate(server, UriKind.Absolute, out Uri? address) && address.Scheme is "http" or "https";

        if (claude is null && codex is null && local is null && !valid)
        {
            return null;
        }

        AiEnvironment defaults = new();
        return defaults with { ClaudeCommand = claude ?? defaults.ClaudeCommand, CodexCommand = codex ?? defaults.CodexCommand, LocalCommand = local ?? defaults.LocalCommand, LocalServer = valid ? server! : defaults.LocalServer };
    }

    private async Task<AiEnvironment> Read(CancellationToken cancellation)
    {
        try
        {
            if (file is not null && await stored.Read(file, cancellation) is { } content)
            {
                return content.Deserialize<AiEnvironment>(options) ?? new AiEnvironment();
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException and not NewerFileException or UnauthorizedAccessException)
        {
        }

        return new AiEnvironment();
    }

    private async Task Write(AiEnvironment environment, CancellationToken cancellation)
    {
        if (file is not null)
        {
            await stored.Write(file, JsonSerializer.SerializeToNode(environment with { HasClaudeApiKey = false, HasCodexApiKey = false, HasOpenAiApiKey = false }, options)!.AsObject(), cancellation);
        }
    }
}
