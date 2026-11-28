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
    /// Gets a value indicating whether the AI is sandboxed: it has no shell and its file tools reach nothing outside the workspace but what the server shares with it. Off unless turned on.
    /// </summary>
    public bool IsSandboxed { get; init; }

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

    /// <summary>
    /// Gets a value indicating whether the chat of a new workspace shows the AI's work (tool calls and their results, dimmed); each workspace can change it afterwards.
    /// </summary>
    public bool ShowWork { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI of a new workspace may delegate tasks to paired servers.
    /// </summary>
    public bool IsDelegationAllowed { get; init; }
}
