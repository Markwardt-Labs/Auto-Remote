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
    /// Gets the command that starts Ollama, which Auto Remote runs itself when the model server is on this machine and not running, so that it can set the variables Ollama only reads when it starts.
    /// </summary>
    public string OllamaCommand { get; init; } = "ollama";

    /// <summary>
    /// Gets a value indicating whether the Ollama that Auto Remote starts runs with a quantized (<c>q4_0</c>) key and value cache and flash attention, which lets the same memory hold a context several times larger. Auto Remote always starts Ollama with no limit on how long a model load may stall.
    /// </summary>
    public bool LocalOptimizeContext { get; init; }

    /// <summary>
    /// Gets a value indicating whether the server can configure the Ollama of the local models as a systemd service (it is Linux with systemd, the model server is on this machine and an <c>ollama.service</c> unit exists), which is the only case the optimized context can be applied to an Ollama that already runs. Filled in when reading and ignored when setting.
    /// </summary>
    public bool OllamaServiceControl { get; init; }

    /// <summary>
    /// Gets a value indicating whether configuring the Ollama service needs the sudo password of the server's user (passwordless sudo does not work for it). Filled in when reading and ignored when setting.
    /// </summary>
    public bool OllamaServiceNeedsPassword { get; init; }

    /// <summary>
    /// Gets what the server can say about the Ollama it runs the local models on (started by Auto Remote with its settings, or already running outside it and so out of reach), or <see langword="null" /> when there is nothing to say. Filled in when reading and ignored when setting.
    /// </summary>
    public string? LocalServerNote { get; init; }

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
    /// Gets how the local AI runs the work of several workspaces: one at a time with a queue, which is the default, or side by side.
    /// </summary>
    public LocalWorkMode LocalWorkMode { get; init; }

    /// <summary>
    /// Gets how many delegated tasks the local AI works on at once when <see cref="LocalWorkMode" /> is <see cref="AutoRemote.LocalWorkMode.Simultaneous" />. From 1 to 16.
    /// </summary>
    public int DelegatedTaskLimit { get; init; } = 1;

    /// <summary>
    /// Gets how often, in minutes, the AI is told to report its status while it works, in every workspace that has no interval of its own. From 1 to 1440.
    /// </summary>
    public int StatusMinutes { get; init; } = 5;

    /// <summary>
    /// Gets the share of a usage window of Claude Code, in percent, at which the AI is told to pause its work in every workspace that uses it. From 1 to 100; 95 by default. The server checks again every 15 minutes and tells the AI to resume once no window of the provider is at or past it.
    /// </summary>
    public double ClaudeMaxWindowPercent { get; init; } = 95;

    /// <summary>
    /// Gets the share of a usage window of OpenAI Codex, in percent, at which the AI is told to pause its work in every workspace that uses it. From 1 to 100; 95 by default.
    /// </summary>
    public double CodexMaxWindowPercent { get; init; } = 95;

    /// <summary>
    /// Gets the maximum usage window share that applies to an AI provider.
    /// </summary>
    /// <param name="service">The provider.</param>
    /// <returns>The maximum, in percent, or <see langword="null" /> for a provider that reports no usage windows, which has none.</returns>
    public double? MaxWindowPercentOf(AiService service) => service switch
    {
        AiService.ClaudeCode => ClaudeMaxWindowPercent,
        AiService.Codex => CodexMaxWindowPercent,
        _ => null
    };

    /// <summary>
    /// Gets the prompt the review button sends in every workspace that has no review prompt of its own. It is never empty: an empty one is replaced by the default.
    /// </summary>
    public string ReviewPrompt { get; init; } = "review workspace and fix any problems or inconsistencies";

    /// <summary>
    /// Gets the prompt the expand button sends in every workspace that has no expand prompt of its own. It is never empty: an empty one is replaced by the default.
    /// </summary>
    public string ExpandPrompt { get; init; } = "expand upon content in workspace in areas that make sense";
}
