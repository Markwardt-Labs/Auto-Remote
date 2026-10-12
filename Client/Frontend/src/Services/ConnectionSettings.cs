namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Where and how the client reaches a server.
/// </summary>
internal sealed record ConnectionSettings
{
    /// <summary>
    /// Gets the server's host name or address.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the server's TCP port.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the access key the server requires.
    /// </summary>
    public required string AccessKey { get; init; }
}
