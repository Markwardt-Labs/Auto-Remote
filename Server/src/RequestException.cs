namespace Markwardt.AutoRemote.Server;

/// <summary>
/// A client request that cannot be carried out; the message is sent back to the client.
/// </summary>
/// <param name="message">What the client should be told.</param>
internal sealed class RequestException(string message) : Exception(message);
