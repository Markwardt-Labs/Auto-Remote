namespace Markwardt.AutoRemote;

/// <summary>
/// How one branch stands against another, for the actions that ask for a commit message or refuse when there is nothing to do.
/// </summary>
public sealed record GitBranchState
{
    /// <summary>
    /// Gets how many commits the branch has that the base does not.
    /// </summary>
    public required int Unique { get; init; }

    /// <summary>
    /// Gets a value indicating whether the base's tip is already part of the branch's history, so the branch is built on top of it.
    /// </summary>
    public required bool IsBased { get; init; }

    /// <summary>
    /// Gets the subject of the branch's tip commit, the default message of a squash.
    /// </summary>
    public string Subject { get; init; } = string.Empty;
}
