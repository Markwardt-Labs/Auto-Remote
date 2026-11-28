namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Tells apart failures that pass, after which the same work can simply be tried again, from failures of the work itself.
/// </summary>
internal interface IFailureKinds
{
    /// <summary>
    /// Checks whether a failure means the AI cannot run for now: an outage, a usage limit, being signed out, or a tool or service that cannot be reached.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns><see langword="true" /> if it does.</returns>
    bool IsPassing(string? error);

    /// <summary>
    /// Gets how long to wait before trying again.
    /// </summary>
    /// <param name="attempt">How often it was put off before, from zero.</param>
    /// <returns>The wait.</returns>
    TimeSpan Delay(int attempt);
}

/// <inheritdoc />
internal sealed class FailureKinds(IReadOnlyList<TimeSpan>? delays = null) : IFailureKinds
{
    private readonly IReadOnlyList<TimeSpan> waits = delays ?? [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30)];

    private readonly Regex passing = new(@"usage limit|limit reached|reached (?:your|the) .*limit|hit your .*limit|rate.?limit|limit exceeded|exceeded (?:your|the) .*limit|quota|credit|overloaded|unavailable|temporar|try again|timed? ?out|network|connect|socket|dns|ECONN|ENOTFOUND|EAI_AGAIN|sign(?:ed)? ?(?:in|out)|log(?:ged)? ?(?:in|out)|authenticat|unauthori[sz]ed|forbidden|api.?key|\b(?:401|403|429|500|502|503|504|529)\b|could not be started|could not start|is not installed|command not found|no such file or directory.*(?:claude|codex|opencode)|model server", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public bool IsPassing(string? error) => error is not null && passing.IsMatch(error);

    /// <inheritdoc />
    public TimeSpan Delay(int attempt) => waits[Math.Clamp(attempt, 0, waits.Count - 1)];
}
