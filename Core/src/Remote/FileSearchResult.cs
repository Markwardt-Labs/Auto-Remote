namespace Markwardt.AutoRemote;

/// <summary>
/// One hit of a file search: a file whose name matched, or a line of a file that contains the text.
/// </summary>
public sealed record FileSearchResult
{
    /// <summary>
    /// Gets the file's path relative to the workspace folder, using <c>/</c> separators.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Gets the one-based number of the matching line, or zero for a match on the file name.
    /// </summary>
    public int Line { get; init; }

    /// <summary>
    /// Gets the trimmed matching line, empty for a match on the file name.
    /// </summary>
    public string Text { get; init; } = string.Empty;
}
