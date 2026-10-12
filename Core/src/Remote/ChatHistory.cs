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
    /// Gets the position in the whole conversation of the first message of the chunk; anything above zero means older messages exist.
    /// </summary>
    public required int Start { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AI is working in the workspace.
    /// </summary>
    public bool Working { get; init; }
}
