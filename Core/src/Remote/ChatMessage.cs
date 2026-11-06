namespace Markwardt.AutoRemote;

/// <summary>
/// One turn of a workspace's conversation with Claude Code.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>
    /// Gets who wrote the message.
    /// </summary>
    public required MessageRole Role { get; init; }

    /// <summary>
    /// Gets the message text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets when the message was written, or <see langword="null" /> when the conversation tool did not store it.
    /// </summary>
    public DateTimeOffset? Time { get; init; }
}

/// <summary>
/// The author of a <see cref="ChatMessage" />.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MessageRole
{
    /// <summary>
    /// The person using the client.
    /// </summary>
    User,

    /// <summary>
    /// Claude Code.
    /// </summary>
    Assistant
}
