namespace Markwardt.AutoRemote;

/// <summary>
/// How the AI of one workspace is run: everything the configuration of its Live AI session holds, and the provider it was made with, which cannot change.
/// </summary>
public sealed record WorkspaceSettings
{
    /// <summary>
    /// Gets the provider of the workspace, chosen when it was made. It is only read; setting it has no effect.
    /// </summary>
    public AiService Service { get; init; }

    /// <summary>
    /// Gets how capable the model should be.
    /// </summary>
    public AiCapability Capability { get; init; } = AiCapability.Balanced;

    /// <summary>
    /// Gets how hard the model should work.
    /// </summary>
    public AiEffort Effort { get; init; } = AiEffort.Medium;

    /// <summary>
    /// Gets a value indicating whether the AI has a shell that runs any command on the server's machine as the server's user.
    /// </summary>
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file tools of the AI may use any path on the server's machine and not only the ones inside the workspace folder.
    /// </summary>
    public bool IsSystemAccessAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may read the web and search it.
    /// </summary>
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may make images, which needs image generation in the environment.
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }

    /// <summary>
    /// Gets custom instructions for the AI, or <see langword="null" /> for none.
    /// </summary>
    public string? Instructions { get; init; }

    /// <summary>
    /// Gets what the session reports about how it is run, written for a person: the provider, and once a prompt has chosen the model, the model and its settings. It is only read; setting it has no effect.
    /// </summary>
    public IReadOnlyDictionary<string, string> Descriptors { get; init; } = new Dictionary<string, string>();
}
