namespace Markwardt.AutoRemote;

/// <summary>
/// An automatic pause of the AI of a workspace because a usage window of its provider reached the maximum the global settings allow.
/// </summary>
public sealed record LimitPause
{
    /// <summary>
    /// Gets the name of the usage window that reached the maximum, as the provider reports it.
    /// </summary>
    public required string Window { get; init; }

    /// <summary>
    /// Gets the share of the window that is used, in percent, as of the last reading.
    /// </summary>
    public required double PercentUsed { get; init; }

    /// <summary>
    /// Gets the maximum, in percent, the window is held to.
    /// </summary>
    public required double MaxPercent { get; init; }

    /// <summary>
    /// Gets when the AI was told to pause.
    /// </summary>
    public required DateTimeOffset PausedAt { get; init; }

    /// <summary>
    /// Gets when the server reads the usage windows again to see whether the AI can resume.
    /// </summary>
    public required DateTimeOffset NextCheckAt { get; init; }
}
