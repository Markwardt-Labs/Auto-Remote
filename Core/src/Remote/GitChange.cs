namespace Markwardt.AutoRemote;

/// <summary>
/// One path that differs in git.
/// </summary>
public sealed record GitChange
{
    /// <summary>
    /// Gets the path relative to the workspace folder, using <c>/</c> separators; an ignored folder ends with <c>/</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets how the path differs.
    /// </summary>
    public required GitChangeStatus Status { get; init; }
}
