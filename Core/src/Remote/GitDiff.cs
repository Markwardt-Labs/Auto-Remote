namespace Markwardt.AutoRemote;

/// <summary>
/// The whole of one file with its changes marked, from the working tree against the head or from one commit against its parent.
/// </summary>
public sealed record GitDiff
{
    /// <summary>
    /// Gets the file's path relative to the workspace folder.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the lines in order.
    /// </summary>
    public required IReadOnlyList<GitDiffLine> Lines { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file is binary, so <see cref="Lines" /> is empty.
    /// </summary>
    public bool IsBinary { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="Lines" /> is only the start of the diff.
    /// </summary>
    public bool Truncated { get; init; }
}
