namespace Markwardt.AutoRemote.Server;

/// <summary>
/// How one task is getting on.
/// </summary>
internal sealed record SwarmTaskProgress
{
    /// <summary>
    /// Gets the identifier of the task.
    /// </summary>
    public required Guid TaskId { get; init; }

    /// <summary>
    /// Gets where it stands.
    /// </summary>
    public required SwarmTaskState State { get; init; }

    /// <summary>
    /// Gets how many messages of the conversation come before these.
    /// </summary>
    public int Offset { get; init; }

    /// <summary>
    /// Gets the messages of the conversation from the offset on.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
}
