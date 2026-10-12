namespace Markwardt.AutoRemote;

/// <summary>
/// The kinds of <see cref="GitRef" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitRefKind
{
    /// <summary>
    /// A local branch.
    /// </summary>
    Branch,

    /// <summary>
    /// A branch of a remote.
    /// </summary>
    Remote,

    /// <summary>
    /// A tag.
    /// </summary>
    Tag
}
