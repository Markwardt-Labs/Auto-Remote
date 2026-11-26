namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A client request that cannot be carried out; the message is sent back to the client.
/// </summary>
/// <param name="message">What the client should be told.</param>
internal sealed class RequestException(string message) : Exception(message)
{
    /// <summary>
    /// Gets a value indicating whether the request failed because the user stopped or cancelled the work while it was starting.
    /// </summary>
    public bool IsHalted { get; init; }

    /// <summary>
    /// Gets a value indicating whether the failure may pass by itself, as when a command of git fails because the network or a lock is in the way.
    /// </summary>
    public bool IsTemporary { get; init; }
}
