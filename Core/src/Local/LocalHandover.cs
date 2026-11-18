namespace Markwardt.AutoRemote;

/// <summary>
/// What a server tells an account it allows on the same machine, through its local channel: everything a client needs to connect the ordinary way.
/// </summary>
public sealed record LocalHandover
{
    /// <summary>
    /// Gets the address to connect to.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Gets the port the server listens on.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the access key of the server.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the version of the server.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the SHA-256 fingerprint of the certificate the server presents, in hexadecimal, which the client must see before it sends the key.
    /// </summary>
    public required string Fingerprint { get; init; }
}
