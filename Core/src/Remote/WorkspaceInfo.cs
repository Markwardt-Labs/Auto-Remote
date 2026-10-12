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
    /// Gets the AI session of <see cref="Service" /> attached to the workspace, or <see langword="null" /> until the first prompt starts one.
    /// </summary>
    public Guid? AiSessionId { get; init; }

    /// <summary>
    /// Gets the group the workspace is organized into, or <see langword="null" /> when it is not in one.
    /// </summary>
    public Guid? GroupId { get; init; }

    /// <summary>
    /// Gets the AI service the workspace's prompts run on.
    /// </summary>
    public AiService Service { get; init; } = AiService.ClaudeCode;

    /// <summary>
    /// Gets the model the workspace's prompts run on.
    /// </summary>
    public AiModel Model { get; init; } = AiModel.Sonnet;

    /// <summary>
    /// Gets the effort the workspace's prompts run with.
    /// </summary>
    public AiEffort Effort { get; init; } = AiEffort.High;

    /// <summary>
    /// Gets the workspace's display name, which is the name of <see cref="Folder" />.
    /// </summary>
    [JsonIgnore]
    public string Name => Folder.TrimEnd('/', '\\').Split('/', '\\')[^1] is { Length: > 0 } name ? name : Folder;
}
