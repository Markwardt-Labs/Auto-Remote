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

    /// <summary>
    /// Gets a value indicating whether the message is work the AI did on the way, a tool call or its result, rather than something it said; clients only show it when the workspace is set to show the AI's work, and then dimmed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsWork { get; init; }

    /// <summary>
    /// Gets a value indicating whether the message is the reasoning of the AI (always work too), which the server keeps like any message and streams while the AI reasons, so a client replaces the streamed text with it, in place, when the reasoning ends.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsThought { get; init; }

    /// <summary>
    /// Gets a value indicating whether this is a prompt a client shows while it is on its way to the server, until the server's copy takes its place. It is the client's own and never sent.
    /// </summary>
    [JsonIgnore]
    public bool IsPending { get; init; }
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
