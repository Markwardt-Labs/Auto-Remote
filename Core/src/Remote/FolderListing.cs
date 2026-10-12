namespace Markwardt.AutoRemote;

/// <summary>
/// The contents of one folder on the server's machine.
/// </summary>
public sealed record FolderListing
{
    /// <summary>
    /// Gets the folder's absolute path, or an empty string for the list of file system roots (drives on Windows).
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the path of the containing folder, or <see langword="null" /> when there is nothing above this folder.
    /// </summary>
    public string? Parent { get; init; }

    /// <summary>
    /// Gets the visible sub-folders, ordered by name.
    /// </summary>
    public required IReadOnlyList<FolderEntry> Folders { get; init; }
}
