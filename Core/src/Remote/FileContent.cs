namespace Markwardt.AutoRemote;

/// <summary>
/// The text of a file inside a workspace's folder.
/// </summary>
public sealed record FileContent
{
    /// <summary>
    /// Gets the file's path relative to the workspace folder.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the file text, cut short when the file is large.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Text" /> is only the start of the file.
    /// </summary>
    public bool Truncated { get; init; }
}
