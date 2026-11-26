namespace Markwardt.AutoRemote;

/// <summary>
/// What the AI of a task that a paired server delegated may do here. Everything is off unless the owner of this server turns it on.
/// </summary>
public sealed record SwarmPermissions
{
    /// <summary>
    /// Gets a value indicating whether the AI may run shell commands, which can reach anything the server's account can, whatever else is off.
    /// </summary>
    public bool IsShellAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file tools may reach files outside the workspace of the task.
    /// </summary>
    public bool IsSystemAccessAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may use the web.
    /// </summary>
    public bool IsWebAllowed { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI may generate images.
    /// </summary>
    public bool IsImageGenerationAllowed { get; init; }
}
