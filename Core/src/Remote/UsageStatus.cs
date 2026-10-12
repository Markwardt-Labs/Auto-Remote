namespace Markwardt.AutoRemote;

/// <summary>
/// The usage limits of an AI service.
/// </summary>
public sealed record UsageStatus
{
    /// <summary>
    /// Gets the AI service the limits belong to.
    /// </summary>
    public required AiService Service { get; init; }

    /// <summary>
    /// Gets the short window, or <see langword="null" /> when the service reports none.
    /// </summary>
    public UsagePeriod? Session { get; init; }

    /// <summary>
    /// Gets the weekly window, or <see langword="null" /> when the service reports none.
    /// </summary>
    public UsagePeriod? Week { get; init; }
}
