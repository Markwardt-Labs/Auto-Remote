namespace Markwardt.AutoRemote;

/// <summary>
/// One commit of a repository's history.
/// </summary>
public sealed record GitCommit
{
    /// <summary>
    /// Gets the full hash.
    /// </summary>
    public required string Hash { get; init; }

    /// <summary>
    /// Gets the first line of the message.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Gets the author's name.
    /// </summary>
    public required string Author { get; init; }

    /// <summary>
    /// Gets when the commit was made.
    /// </summary>
    public required DateTimeOffset Date { get; init; }

    /// <summary>
    /// Gets the full hashes of the commit's parents, the first being the one it continues.
    /// </summary>
    public IReadOnlyList<string> Parents { get; init; } = [];

    /// <summary>
    /// Gets the branches and tags that point at the commit.
    /// </summary>
    public IReadOnlyList<GitRef> Refs { get; init; } = [];
}
