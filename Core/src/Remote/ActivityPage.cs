namespace Markwardt.AutoRemote;

/// <summary>
/// A page of the activity of a workspace.
/// </summary>
public sealed record ActivityPage
{
    /// <summary>
    /// Gets the entries, the newest first.
    /// </summary>
    public required IReadOnlyList<ActivityEntry> Entries { get; init; }

    /// <summary>
    /// Gets the cursor to read older entries with, or <see langword="null" /> when there are none.
    /// </summary>
    public string? Older { get; init; }
}
