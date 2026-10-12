namespace Markwardt.AutoRemote;

/// <summary>
/// One line of a file's diff, a single merged sequence of the file before and after.
/// </summary>
public sealed record GitDiffLine
{
    /// <summary>
    /// Gets whether the line was added, removed or is unchanged.
    /// </summary>
    public required GitDiffKind Kind { get; init; }

    /// <summary>
    /// Gets the line's text without its terminator.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the line number before the change, or <see langword="null" /> for an added line.
    /// </summary>
    public int? OldNumber { get; init; }

    /// <summary>
    /// Gets the line number after the change, or <see langword="null" /> for a removed line.
    /// </summary>
    public int? NewNumber { get; init; }
}
