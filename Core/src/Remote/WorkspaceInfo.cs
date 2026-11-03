namespace Markwardt.AutoRemote;

/// <summary>
/// A workspace hosted by the server, bound to a folder on the server's machine.
/// </summary>
public sealed record WorkspaceInfo
{
    /// <summary>
    /// Gets the workspace's unique identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the absolute path, on the server's machine, of the folder hosting the workspace.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Gets the identifier of the live AI session of <see cref="Service" /> attached to the workspace (32 hexadecimal digits), or <see langword="null" /> until the first prompt starts one. A workspace stored by an older server holds the AI service's own session identifier here, written with hyphens, which the server turns into a live session when it is first used.
    /// </summary>
    public string? AiSessionId { get; init; }

    /// <summary>
    /// Gets the group the workspace is organized into, or <see langword="null" /> when it is not in one.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Gets the AI service the workspace's prompts run on. Its model and the settings of the model belong to the AI session and are read with <see cref="RemoteCommand.GetWorkspaceAi" />.
    /// </summary>
    public AiService Service { get; init; } = AiService.ClaudeCode;

    /// <summary>
    /// Gets the workspace's display name, which is the name of <see cref="Folder" />.
    /// </summary>
    [JsonIgnore]
    public string Name => Folder.TrimEnd('/', '\\').Split('/', '\\')[^1] is { Length: > 0 } name ? name : Folder;
}
