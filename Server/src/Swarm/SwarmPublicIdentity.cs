namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The public keys of a server in the swarm, which is who it is: the identifier and the fingerprint are derived from them.
/// </summary>
internal sealed record SwarmPublicIdentity
{
    /// <summary>
    /// Gets the public ECDSA key that checks its signatures, as base64 of the subject public key info.
    /// </summary>
    public required string SigningKey { get; init; }

    /// <summary>
    /// Gets the public ECDH key that packages for it are encrypted to, as base64 of the subject public key info.
    /// </summary>
    public required string ExchangeKey { get; init; }

    /// <summary>
    /// Gets the identifier: the start of the hash of both keys, as hexadecimal digits.
    /// </summary>
    [JsonIgnore]
    public string Id => Hash()[..32];

    /// <summary>
    /// Gets the fingerprint people compare: the start of the identifier in groups of four.
    /// </summary>
    [JsonIgnore]
    public string Fingerprint => string.Join('-', Enumerable.Range(0, 5).Select(group => Id.Substring(group * 4, 4).ToUpperInvariant()));

    private string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{SigningKey}\n{ExchangeKey}")));
}
