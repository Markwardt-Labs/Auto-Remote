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
    /// Gets every window the service reports, in the order it reports them; empty when it reports none.
    /// </summary>
    public IReadOnlyList<UsagePeriod> Windows { get; init; } = [];
}
