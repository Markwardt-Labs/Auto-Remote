namespace Markwardt.AutoRemote.Server;

/// <summary>
/// One thing a <see cref="IAiConversation" /> reports as the AI works.
/// </summary>
internal sealed record AiOutput
{
    /// <summary>
    /// Gets what happened.
    /// </summary>
    public required AiOutputKind Kind { get; init; }

    /// <summary>
    /// Gets the reply text for <see cref="AiOutputKind.Reply" />, the failure for <see cref="AiOutputKind.Failed" /> or the session identifier for <see cref="AiOutputKind.Session" />.
    /// </summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>
/// The kinds of <see cref="AiOutput" />.
/// </summary>
internal enum AiOutputKind
{
    /// <summary>
    /// The AI wrote text for the user.
    /// </summary>
    Reply,

    /// <summary>
    /// The AI finished all the work it was given and waits for more.
    /// </summary>
    Finished,

    /// <summary>
    /// The AI reported a failure.
    /// </summary>
    Failed,

    /// <summary>
    /// The service named the AI session that the conversation runs in, for services that choose the identifier themselves.
    /// </summary>
    Session
}
