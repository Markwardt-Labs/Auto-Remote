namespace Markwardt.AutoRemote;

/// <summary>
/// A sub-folder shown while browsing the server's file system.
/// </summary>
public sealed record FolderEntry
{
    /// <summary>
    /// Gets the folder's display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the folder's absolute path on the server's machine.
    /// </summary>
    public required string Path { get; init; }
}
