namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What a workspace does by itself right now.
/// </summary>
/// <param name="IsCommitting">Whether a commit is under way.</param>
/// <param name="IsCommitDue">Whether a commit is still to come once the work is over.</param>
/// <param name="IsStopped">Whether the user stopped what runs by itself.</param>
/// <param name="IsAwaited">Whether tasks its AI delegated are under way.</param>
/// <param name="Step">The current work, or <see langword="null" />.</param>
/// <param name="Retry">The work to try again, or <see langword="null" />.</param>
/// <param name="RetryAt">When it is tried again.</param>
/// <param name="RetryReason">Why it waits.</param>
/// <param name="HoldUntil">Until when the queued inputs wait after one could not be sent.</param>
/// <param name="HoldReason">Why they wait.</param>
internal sealed record FollowState(bool IsCommitting, bool IsCommitDue, bool IsStopped, bool IsAwaited, WorkStep? Step, WorkStep? Retry, DateTimeOffset? RetryAt, string? RetryReason, DateTimeOffset? HoldUntil, string? HoldReason);
