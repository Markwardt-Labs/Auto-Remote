namespace Markwardt.AutoRemote;

/// <summary>
/// What the AI session of a new workspace starts with, which is the environment the server applies to every session it makes. A workspace keeps the values it was created with whatever this is changed to later, and its own settings (<see cref="WorkspaceSettings" />) change them.
/// </summary>
public sealed record WorkspaceDefaults
{
    /// <summary>
    /// Gets how capable the model of a new session is.
    /// </summary>
    public AiCapability Capability { get; init; } = AiCapability.Balanced;

    /// <summary>
    /// Gets how hard the model of a new session works.
    /// </summary>
    public AiEffort Effort { get; init; } = AiEffort.High;

    /// <summary>
    /// Gets a value indicating whether the AI of a new session may run shell commands.
    /// </summary>
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI's file tools in a new session may use any path on the server's machine and not only the ones inside the workspace folder.
    /// </summary>
    public bool IsSystemAccessAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI of a new session may use the web.
    /// </summary>
    public bool IsWebAllowed { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether the AI of a new session may generate images, which it can only do while the environment has an image generation service. Off by default.
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }

    /// <summary>
    /// Gets the standing instructions of a new session, empty for none (a missing value would not survive the wire, which leaves out what is <see langword="null" />).
    /// </summary>
    public string Instructions { get; init; } = string.Empty;
}
