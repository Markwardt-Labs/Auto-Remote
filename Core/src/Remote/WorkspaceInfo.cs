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
    /// Gets the identifier of the live AI session of <see cref="Service" /> that holds the workspace's folder, provider and AI configuration (32 hexadecimal digits). Informational: the server manages it, and replaces the session, keeping the workspace, when the session can no longer be used.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the group the workspace is organized into, or <see langword="null" /> when it is not in one.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Gets the provider the workspace's prompts run on, chosen when the workspace was made and permanent. How its AI is run belongs to the AI session and is read with <see cref="RemoteCommand.GetWorkspaceSettings" />.
    /// </summary>
    public AiService Service { get; init; } = AiService.ClaudeCode;

    /// <summary>
    /// Gets the workspace's display name, which is the name of <see cref="Folder" />.
    /// </summary>
    [JsonIgnore]
    public string Name => Folder.TrimEnd('/', '\\').Split('/', '\\')[^1] is { Length: > 0 } name ? name : Folder;
}
