namespace Markwardt.AutoRemote;

/// <summary>
/// One piece of work of a workspace: what started it, how it went and what came of it. It is a record to find what happened, not a backup.
/// </summary>
public sealed record ActivityEntry
{
    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Gets when the work started.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Gets when it ended, or <see langword="null" /> while it has not.
    /// </summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Gets what started it.
    /// </summary>
    public required ActivitySource Source { get; init; }

    /// <summary>
    /// Gets a short line naming it.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets what the AI was asked, cut when it is long.
    /// </summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Gets how it went.
    /// </summary>
    public ActivityOutcome Outcome { get; init; } = ActivityOutcome.Running;

    /// <summary>
    /// Gets why it did not end well, or <see langword="null" />.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Gets the last reply of the AI, which says what it did and assumed, or <see langword="null" />.
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// Gets the subject of the commit made after it, or <see langword="null" />.
    /// </summary>
    public string? Commit { get; init; }

    /// <summary>
    /// Gets whether the commit was pushed, or <see langword="null" /> when nothing was committed.
    /// </summary>
    public bool? Pushed { get; init; }

    /// <summary>
    /// Gets why the commit or its push failed, or <see langword="null" />.
    /// </summary>
    public string? CommitError { get; init; }

    /// <summary>
    /// Gets what the sessions or tasks that were brought in said about their work, or <see langword="null" />.
    /// </summary>
    public string? Handoff { get; init; }

    /// <summary>
    /// Gets the files those changed.
    /// </summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// Gets what else happened to it, such as retries, restarts, delegations and stops, oldest first.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>
    /// Gets a value indicating whether there is something for the user to look at. It holds nothing up.
    /// </summary>
    public bool NeedsAttention { get; init; }
}
