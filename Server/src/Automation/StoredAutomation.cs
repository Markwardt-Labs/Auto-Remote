namespace Markwardt.AutoRemote.Server;

/// <summary>
/// What one workspace does by itself, as kept on disk.
/// </summary>
internal sealed record StoredAutomation
{
    /// <summary>
    /// Gets a value indicating whether the user stopped it, which holds everything that runs by itself.
    /// </summary>
    public bool IsStopped { get; init; }

    /// <summary>
    /// Gets a value indicating whether the changes are to be committed when the current work ends.
    /// </summary>
    public bool IsArmed { get; init; }

    /// <summary>
    /// Gets a value indicating whether a commit is still to be made for work that ended.
    /// </summary>
    public bool IsCommitDue { get; init; }

    /// <summary>
    /// Gets what the user chose for the commit of the current work, or <see langword="null" /> when they chose nothing.
    /// </summary>
    public bool? UserChoice { get; init; }

    /// <summary>
    /// Gets a value indicating whether the commit was turned on only because something to commit was queued behind the current work.
    /// </summary>
    public bool IsArmedByQueue { get; init; }

    /// <summary>
    /// Gets the current work, or <see langword="null" />.
    /// </summary>
    public WorkStep? Step { get; init; }

    /// <summary>
    /// Gets the work that is to be tried again, or <see langword="null" />.
    /// </summary>
    public WorkStep? Retry { get; init; }

    /// <summary>
    /// Gets further work that waits to be tried again after it.
    /// </summary>
    public IReadOnlyList<WorkStep> Backlog { get; init; } = [];

    /// <summary>
    /// Gets when it is tried again.
    /// </summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>
    /// Gets why it waits to be tried again.
    /// </summary>
    public string? RetryReason { get; init; }

    /// <summary>
    /// Gets what the AI is still to be told about work that did not end well.
    /// </summary>
    public IReadOnlyList<string> Notices { get; init; } = [];
}
