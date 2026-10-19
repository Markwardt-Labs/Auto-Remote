namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How a git repository workspace looked when AI work started in it, which is what cancelling that work puts back.
/// </summary>
internal sealed record WorkspaceSnapshot
{
    /// <summary>
    /// Gets the workspace folder.
    /// </summary>
    public required string Folder { get; init; }

    /// <summary>
    /// Gets the commit the head pointed at, or <see langword="null" /> before the first commit.
    /// </summary>
    public string? Head { get; init; }

    /// <summary>
    /// Gets the branch the head was on, or <see langword="null" /> when it was detached.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>
    /// Gets the tree object holding what was staged.
    /// </summary>
    public required string IndexTree { get; init; }

    /// <summary>
    /// Gets the tree object holding every file git does not ignore, untracked ones included.
    /// </summary>
    public required string Tree { get; init; }
}
