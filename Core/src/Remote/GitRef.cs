namespace Markwardt.AutoRemote;

/// <summary>
/// A branch or tag pointing at a commit.
/// </summary>
public sealed record GitRef
{
    /// <summary>
    /// Gets the short name of the branch or tag.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets what kind of reference this is.
    /// </summary>
    public required GitRefKind Kind { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is the branch that is checked out.
    /// </summary>
    public bool IsCurrent { get; init; }
}
