namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How a piece of work ended, as told to whoever asked for it.
/// </summary>
/// <param name="Outcome">How it went; <see cref="ActivityOutcome.Waiting" /> when the AI could not run.</param>
/// <param name="CouldNotRun">Whether the AI could not run at all, so nothing of the work was done.</param>
/// <param name="Error">Why it did not end well, or <see langword="null" />.</param>
internal sealed record WorkEnd(ActivityOutcome Outcome, bool CouldNotRun = false, string? Error = null);
