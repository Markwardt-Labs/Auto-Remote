namespace Markwardt.AutoRemote;

/// <summary>
/// The contents of one folder inside a workspace's folder.
/// </summary>
public sealed record WorkspaceFiles
{
    /// <summary>
    /// Gets the folder's path relative to the workspace folder, empty for the workspace folder itself.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the relative path of the containing folder, or <see langword="null" /> at the workspace folder.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>
    /// Gets the entries, folders first and then files, each ordered by name.
    /// </summary>
    public required IReadOnlyList<FileEntry> Entries { get; init; }
}
