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
    Tag,

    /// <summary>
    /// The branch of a side session, named after the session. It is not a branch to check out, merge or delete.
    /// </summary>
    Session
}
