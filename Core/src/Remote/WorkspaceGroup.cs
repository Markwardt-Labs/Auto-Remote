namespace Markwardt.AutoRemote;

/// <summary>
/// A named group that workspaces are organized into; which can contain other groups. Groups only organize workspaces in Auto Remote and are unrelated to folders on disk.
/// </summary>
public sealed record WorkspaceGroup
{
    /// <summary>
    /// Gets the group's unique identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets the group's display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the group this group is inside, or <see langword="null" /> at the top level.
    /// </summary>
    public Guid? ParentId { get; init; }
}
