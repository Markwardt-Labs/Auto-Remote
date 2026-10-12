namespace Markwardt.AutoRemote;

/// <summary>
/// The kinds of <see cref="GitDiffLine" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitDiffKind
{
    /// <summary>
    /// The line is the same before and after.
    /// </summary>
    Unchanged,

    /// <summary>
    /// The line was added.
    /// </summary>
    Added,

    /// <summary>
    /// The line was removed.
    /// </summary>
    Removed
}
