namespace Markwardt.AutoRemote.Server;

/// <summary>
/// An invitation or acceptance to pair, as it is written to a drive: public keys and a signature with them, which shows the writer holds the private keys. A person still compares the fingerprint, which is what proves who the writer is.
/// </summary>
internal sealed record SwarmPairingFile
{
    /// <summary>
    /// Gets the public keys of the writer.
    /// </summary>
    public required SwarmPublicIdentity Identity { get; init; }

    /// <summary>
    /// Gets the name of the writer.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the identifier of the server an acceptance is for; empty for an invitation.
    /// </summary>
    public string For { get; init; } = string.Empty;

    /// <summary>
    /// Gets when it was written.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Gets the signature of the rest, as base64.
    /// </summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Gets what the signature covers.
    /// </summary>
    [JsonIgnore]
    public ReadOnlyMemory<byte> Signed => Encoding.UTF8.GetBytes(string.Join('\n', "auto-remote pairing", Identity.SigningKey, Identity.ExchangeKey, Name, For, CreatedAt.ToString("O", CultureInfo.InvariantCulture)));

    /// <summary>
    /// Checks the signature.
    /// </summary>
    /// <returns><see langword="true" /> if the writer holds the signing key.</returns>
    public bool Verify()
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Identity.SigningKey), out _);
            return key.VerifyData(Signed.Span, Convert.FromBase64String(Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
