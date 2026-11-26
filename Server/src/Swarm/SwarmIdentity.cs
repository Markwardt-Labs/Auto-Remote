namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The identity keys of this server: it signs what it sends with them and opens what is encrypted to it.
/// </summary>
internal interface ISwarmIdentity : IDisposable
{
    /// <summary>
    /// Gets the public keys.
    /// </summary>
    SwarmPublicIdentity Public { get; }

    /// <summary>
    /// Signs data with the private signing key.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>The signature.</returns>
    ReadOnlyMemory<byte> Sign(ReadOnlySpan<byte> data);

    /// <summary>
    /// Derives the secret shared with another server, from which package keys are made.
    /// </summary>
    /// <param name="other">The other server's public identity.</param>
    /// <returns>The shared secret.</returns>
    /// <exception cref="InvalidDataException">The other key is not usable.</exception>
    byte[] Share(SwarmPublicIdentity other);

    /// <summary>
    /// Makes and loads identities.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Loads the identity a file holds, making one and storing it there, readable by the server's account only, when the file does not exist.
        /// </summary>
        /// <param name="path">The file.</param>
        /// <param name="cancellation">Cancels the work.</param>
        /// <returns>The identity.</returns>
        /// <exception cref="InvalidDataException">The file is damaged.</exception>
        Task<ISwarmIdentity> Load(string path, CancellationToken cancellation);
    }

    /// <inheritdoc />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public async Task<ISwarmIdentity> Load(string path, CancellationToken cancellation)
        {
            if (File.Exists(path))
            {
                try
                {
                    JsonElement stored = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellation)).RootElement;
                    ECDsa signing = ECDsa.Create();
                    signing.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.GetProperty("signing").GetString()!), out _);
                    ECDiffieHellman exchange = ECDiffieHellman.Create();
                    exchange.ImportPkcs8PrivateKey(Convert.FromBase64String(stored.GetProperty("exchange").GetString()!), out _);
                    return new SwarmIdentity(signing, exchange);
                }
                catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException or KeyNotFoundException or InvalidOperationException or ArgumentNullException)
                {
                    throw new InvalidDataException($"The swarm identity '{path}' is damaged: {exception.Message}", exception);
                }
            }

            SwarmIdentity created = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
            string content = JsonSerializer.Serialize(created.Export());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using FileStream file = new(path, options);
            await file.WriteAsync(Encoding.UTF8.GetBytes(content), cancellation);
            return created;
        }
    }
}

/// <inheritdoc />
internal sealed class SwarmIdentity : ISwarmIdentity
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SwarmIdentity" /> class.
    /// </summary>
    /// <param name="signing">The signing key.</param>
    /// <param name="exchange">The key agreement key.</param>
    public SwarmIdentity(ECDsa signing, ECDiffieHellman exchange)
    {
        this.signing = signing;
        this.exchange = exchange;
        Public = new SwarmPublicIdentity { SigningKey = Convert.ToBase64String(signing.ExportSubjectPublicKeyInfo()), ExchangeKey = Convert.ToBase64String(exchange.PublicKey.ExportSubjectPublicKeyInfo()) };
    }

    private readonly ECDsa signing;

    private readonly ECDiffieHellman exchange;

    private readonly Lock gate = new();

    /// <inheritdoc />
    public SwarmPublicIdentity Public { get; }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Sign(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            return signing.SignData(data, HashAlgorithmName.SHA256);
        }
    }

    /// <inheritdoc />
    public byte[] Share(SwarmPublicIdentity other)
    {
        try
        {
            using ECDiffieHellman theirs = ECDiffieHellman.Create();
            theirs.ImportSubjectPublicKeyInfo(Convert.FromBase64String(other.ExchangeKey), out _);

            lock (gate)
            {
                return exchange.DeriveKeyFromHash(theirs.PublicKey, HashAlgorithmName.SHA256);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The key of the other server cannot be used.", exception);
        }
    }

    /// <summary>
    /// Gets the private keys as they are stored.
    /// </summary>
    /// <returns>The keys by name.</returns>
    public IReadOnlyDictionary<string, string> Export() => new Dictionary<string, string> { ["signing"] = Convert.ToBase64String(signing.ExportPkcs8PrivateKey()), ["exchange"] = Convert.ToBase64String(exchange.ExportPkcs8PrivateKey()) };

    /// <inheritdoc />
    public void Dispose()
    {
        signing.Dispose();
        exchange.Dispose();
    }
}
