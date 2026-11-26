namespace Markwardt.AutoRemote;

/// <summary>
/// Everything about one delegated task.
/// </summary>
public sealed record SwarmTaskDetail
{
    /// <summary>
    /// Gets the task.
    /// </summary>
    public required SwarmTaskInfo Task { get; init; }

    /// <summary>
    /// Gets what the worker was asked to do.
    /// </summary>
    public required string Instructions { get; init; }

    /// <summary>
    /// Gets what the result is expected to be.
    /// </summary>
    public string ExpectedResult { get; init; } = string.Empty;

    /// <summary>
    /// Gets the worker's whole report once it finished.
    /// </summary>
    public string? Report { get; init; }

    /// <summary>
    /// Gets the conversation of the worker, with its work, as far as it has arrived.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}
