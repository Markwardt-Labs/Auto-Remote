namespace Markwardt.AutoRemote;

/// <summary>
/// One chunk of a workspace's conversation and whether the AI is working on it right now.
/// </summary>
public sealed record ChatHistory
{
    /// <summary>
    /// Gets the messages of the chunk, oldest first.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>
    /// Gets the cursor to give as <see cref="RemoteRequest.Before" /> to read the messages before this chunk, or <see langword="null" /> when there are none.
    /// </summary>
    public string? Older { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace.
    /// </summary>
    public bool Working { get; init; }
}
