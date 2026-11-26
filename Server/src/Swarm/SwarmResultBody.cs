namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The content of a result package; the blob holds the git bundle of the changes when the code travels as bundles.
/// </summary>
internal sealed record SwarmResultBody
{
    /// <summary>
    /// Gets the identifier of the task.
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// Gets how it ended.
    /// </summary>
    public required SwarmOutcome Outcome { get; init; }

    /// <summary>
    /// Gets the worker's report.
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// Gets why it did not succeed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Gets the commit holding the changes, whose parent is the base, or <see langword="null" /> when nothing changed.
    /// </summary>
    public string? ResultCommit { get; init; }

    /// <summary>
    /// Gets the conversation of the worker.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}
