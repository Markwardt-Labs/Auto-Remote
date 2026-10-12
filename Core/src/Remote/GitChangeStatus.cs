namespace Markwardt.AutoRemote;

/// <summary>
/// How a path differs in git.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitChangeStatus
{
    /// <summary>
    /// The path is new: untracked, or added by the commit.
    /// </summary>
    Added,

    /// <summary>
    /// The path changed, including being renamed (reported by its new path).
    /// </summary>
    Modified,

    /// <summary>
    /// The path was removed.
    /// </summary>
    Deleted,

    /// <summary>
    /// Git ignores the path; only reported for the working tree, never as part of a commit.
    /// </summary>
    Ignored
}
