namespace Markwardt.AutoRemote;

/// <summary>
/// A chunk of a repository's history, newest commit first.
/// </summary>
public sealed record GitHistory
{
    /// <summary>
    /// Gets the commits of the chunk.
    /// </summary>
    public required IReadOnlyList<GitCommit> Commits { get; init; }

    /// <summary>
    /// Gets a value indicating whether older commits exist beyond the chunk.
    /// </summary>
    public bool HasMore { get; init; }
}
