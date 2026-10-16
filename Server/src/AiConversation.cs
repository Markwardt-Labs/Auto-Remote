namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A live AI conversation, backed by a process of the chosen service. Prompts can be sent at any time, including while it works, and join the work in progress.
/// </summary>
internal interface IAiConversation : IAsyncDisposable
{
    /// <summary>
    /// Sends a prompt to the AI.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="cancellation">Cancels the write.</param>
    /// <exception cref="RequestException">The AI is no longer running.</exception>
    Task Send(string text, CancellationToken cancellation);

    /// <summary>
    /// Ends the AI's process at once, without waiting for the work in progress.
    /// </summary>
    void Kill();

    /// <summary>
    /// Reports what the AI does until its process ends.
    /// </summary>
    /// <param name="cancellation">Stops reading.</param>
    /// <returns>The outputs in order; the sequence ends when the process exits.</returns>
    IAsyncEnumerable<AiOutput> Read(CancellationToken cancellation);
}

