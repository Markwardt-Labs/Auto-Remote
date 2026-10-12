namespace Markwardt.AutoRemote;

/// <summary>
/// A file or folder inside a workspace's folder.
/// </summary>
public sealed record FileEntry
{
    /// <summary>
    /// Gets the entry's name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the entry's path relative to the workspace folder, using <c>/</c> separators.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets a value indicating whether the entry is a folder.
    /// </summary>
    public required bool IsFolder { get; init; }

    /// <summary>
    /// Gets the file size in bytes, or zero for a folder.
    /// </summary>
    public long Size { get; init; }
}
