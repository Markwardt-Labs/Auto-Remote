namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The places a search found in a text.
/// </summary>
internal sealed record TextMatches
{
    /// <summary>
    /// Gets the index of the start of each match, in order.
    /// </summary>
    public required IReadOnlyList<int> Starts { get; init; }

    /// <summary>
    /// Gets the length of every match.
    /// </summary>
    public required int Length { get; init; }

    /// <summary>
    /// Gets the position in <see cref="Starts" /> of the current match, or -1 for none.
    /// </summary>
    public required int Current { get; init; }

    /// <summary>
    /// Gets a value indicating whether the current match should be scrolled into view.
    /// </summary>
    public bool Navigate { get; init; }
}
