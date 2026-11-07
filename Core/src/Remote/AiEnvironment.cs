namespace Markwardt.AutoRemote;

/// <summary>
/// Where and how the AI providers run, for every workspace of the server: the commands and folders of the tools, the keys they use unless a workspace has its own, the local model server and the image generation service. An API key is never sent to a client: a client reads whether one is set and sends a new one to replace it.
/// </summary>
public sealed record AiEnvironment
{
    /// <summary>
    /// Gets the provider that new workspaces start with.
    /// </summary>
    public AiService DefaultService { get; init; } = AiService.ClaudeCode;

    /// <summary>
    /// Gets the command that starts Claude Code.
    /// </summary>
    public string ClaudeCommand { get; init; } = "claude";

    /// <summary>
    /// Gets the folder Claude Code keeps its configuration in, or <see langword="null" /> for its own default.
    /// </summary>
    public string? ClaudeConfigFolder { get; init; }

    /// <summary>
    /// Gets a value indicating whether the environment holds an API key for Claude Code, which workspaces without a key of their own use.
    /// </summary>
    public bool HasClaudeApiKey { get; init; }

    /// <summary>
    /// Gets the new API key for Claude Code when setting the environment: <see langword="null" /> leaves the key as it is and an empty text removes it. It is never filled in when reading.
    /// </summary>
    public string? ClaudeApiKey { get; init; }

    /// <summary>
    /// Gets the command that starts Codex.
    /// </summary>
    public string CodexCommand { get; init; } = "codex";

    /// <summary>
    /// Gets the folder Codex keeps its configuration in, or <see langword="null" /> for its own default.
    /// </summary>
    public string? CodexConfigFolder { get; init; }

    /// <summary>
    /// Gets a value indicating whether the environment holds an API key for Codex, which workspaces without a key of their own use.
    /// </summary>
    public bool HasCodexApiKey { get; init; }

    /// <summary>
    /// Gets the new API key for Codex when setting the environment: <see langword="null" /> leaves the key as it is and an empty text removes it. It is never filled in when reading.
    /// </summary>
    public string? CodexApiKey { get; init; }

    /// <summary>
    /// Gets the command that starts OpenCode, which runs the local models.
    /// </summary>
    public string LocalCommand { get; init; } = "opencode";

    /// <summary>
    /// Gets the address of the model server the local models run on.
    /// </summary>
    public string LocalServer { get; init; } = "http://localhost:11434/v1";

    /// <summary>
    /// Gets the model to run for each capability. Local models cannot run without one for every capability.
    /// </summary>
    public CapabilityNames LocalModels { get; init; } = new();

    /// <summary>
    /// Gets the size of the context window for each capability, or <see langword="null" /> for the server's own.
    /// </summary>
    public CapabilityContexts? LocalContexts { get; init; }

    /// <summary>
    /// Gets the service that makes the images the AI asks for.
    /// </summary>
    public ImageGenerationKind ImageGeneration { get; init; }

    /// <summary>
    /// Gets a value indicating whether the environment holds an API key for OpenAI image generation.
    /// </summary>
    public bool HasOpenAiApiKey { get; init; }

    /// <summary>
    /// Gets the new API key for OpenAI image generation when setting the environment: <see langword="null" /> leaves the key as it is and an empty text removes it. It is never filled in when reading.
    /// </summary>
    public string? OpenAiApiKey { get; init; }

    /// <summary>
    /// Gets the address of the Stable Diffusion server for local image generation.
    /// </summary>
    public string LocalImageServer { get; init; } = "http://localhost:7860";

    /// <summary>
    /// Gets the checkpoint of the Stable Diffusion server to use for each capability.
    /// </summary>
    public CapabilityNames LocalImageModels { get; init; } = new();

    /// <summary>
    /// Gets what the AI session of a new workspace starts with.
    /// </summary>
    public WorkspaceDefaults NewWorkspaces { get; init; } = new();

    /// <summary>
    /// Gets how often, in minutes, the AI is told to report its status with its status tool while it works, in every workspace that has no interval of its own. From 1 to 1440.
    /// </summary>
    public int StatusMinutes { get; init; } = 5;

    /// <summary>
    /// Gets the prompt the review button sends in every workspace that has no review prompt of its own. It is never empty: an empty one is replaced by the default.
    /// </summary>
    public string ReviewPrompt { get; init; } = "review workspace and fix any problems or inconsistencies";

    /// <summary>
    /// Gets the prompt the expand button sends in every workspace that has no expand prompt of its own. It is never empty: an empty one is replaced by the default.
    /// </summary>
    public string ExpandPrompt { get; init; } = "expand upon content in workspace in areas that make sense";
}
