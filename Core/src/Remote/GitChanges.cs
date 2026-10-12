namespace Markwardt.AutoRemote;

/// <summary>
/// The paths that differ in git, in the working tree or in one commit.
/// </summary>
public sealed record GitChanges
{
    /// <summary>
    /// Gets the differing paths, ordered by path.
    /// </summary>
    public required IReadOnlyList<GitChange> Changes { get; init; }
}
