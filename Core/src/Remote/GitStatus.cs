namespace Markwardt.AutoRemote;

/// <summary>
/// The git state of a workspace's folder.
/// </summary>
public sealed record GitStatus
{
    /// <summary>
    /// Gets a value indicating whether the workspace's folder is the root of a git repository; every other member is meaningful only when it is.
    /// </summary>
    public required bool IsRepo { get; init; }

    /// <summary>
    /// Gets the checked out branch, or <see langword="null" /> when the head is detached.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Gets the tag at the head, or <see langword="null" /> when there is none.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Gets the abbreviated hash of the head commit, empty before the first commit.
    /// </summary>
    public string Commit { get; init; } = string.Empty;

    /// <summary>
    /// Gets the subject of the head commit, empty before the first commit.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the working tree has changes that are not committed.
    /// </summary>
    public bool HasChanges { get; init; }

    /// <summary>
    /// Gets a value indicating whether git knows who commits, which commits, tags and rewrites of history need.
    /// </summary>
    public bool HasIdentity { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a git action is running in the workspace right now.
    /// </summary>
    public bool Busy { get; init; }

    /// <summary>
    /// Gets the URL of the <c>origin</c> remote, or <see langword="null" /> when there is none.
    /// </summary>
    public string? Remote { get; init; }
}
