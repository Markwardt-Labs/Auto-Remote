namespace Markwardt.AutoRemote;

/// <summary>
/// What the AI of a task that a paired server delegated may do here. Everything is off unless the owner of this server turns it on.
/// </summary>
public sealed record SwarmPermissions
{
    /// <summary>
    /// Gets a value indicating whether the AI is sandboxed: it has no shell and its file tools reach nothing outside the workspace but what the server shares with it. Off unless turned on.
    /// </summary>
    public bool IsSandboxed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may use the web.
    /// </summary>
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may generate images.
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }
}
