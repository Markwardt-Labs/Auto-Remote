namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One usage window of an AI service as the usage panel shows it.
/// </summary>
internal sealed record UsageLine
{
    /// <summary>
    /// Gets the name the service gives the window.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets how much of the window is used as text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets when the window resets as text, or an empty text when unknown.
    /// </summary>
    public required string Reset { get; init; }

    /// <summary>
    /// Gets the pace of the window's usage, which colors the text: <c>VeryLow</c> green, <c>Low</c> blue, <c>Even</c> normal, <c>High</c> orange and <c>VeryHigh</c> red.
    /// </summary>
    public required string Pace { get; init; }
}
