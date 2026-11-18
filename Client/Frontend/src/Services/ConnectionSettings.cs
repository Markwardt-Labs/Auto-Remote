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

    /// <summary>
    /// Gets a value indicating whether the server is one on this machine that is asked for its port and access key through its local channel every time the app connects; <see cref="Port" /> then says which server, and the key is not kept.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Local { get; init; }

    /// <summary>
    /// Gets the SHA-256 fingerprint, in hexadecimal, that the certificate of the server must have, or <see langword="null" /> to accept any certificate. Only set for the details a local server just handed over, never saved.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Fingerprint { get; init; }
}
