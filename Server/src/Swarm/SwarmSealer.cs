namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Seals packages between paired servers, encrypted so only the receiver reads them and signed so the receiver knows who made them, and opens them again.
/// </summary>
internal interface ISwarmSealer
{
    /// <summary>
    /// Seals a package from this server.
    /// </summary>
    /// <param name="sender">The identity of this server.</param>
    /// <param name="receiver">The public identity of the receiver.</param>
    /// <param name="id">The identifier of the package.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="body">Its content as JSON.</param>
    /// <param name="blob">Binary data it carries.</param>
    /// <param name="createdAt">When it is made.</param>
    /// <returns>The sealed bytes.</returns>
    ReadOnlyMemory<byte> Seal(ISwarmIdentity sender, SwarmPublicIdentity receiver, Guid id, SwarmMessageKind kind, ReadOnlySpan<byte> body, ReadOnlySpan<byte> blob, DateTimeOffset createdAt);

    /// <summary>
    /// Reads who made a sealed package, which is not proven until it is opened.
    /// </summary>
    /// <param name="data">The sealed bytes.</param>
    /// <returns>The header.</returns>
    /// <exception cref="InvalidDataException">It is not a package.</exception>
    SwarmEnvelopeHeader Peek(ReadOnlySpan<byte> data);

    /// <summary>
    /// Opens a sealed package: checks that its sender signed it, that it is addressed to this server and decrypts it.
    /// </summary>
    /// <param name="receiver">The identity of this server.</param>
    /// <param name="senderOf">Finds the public identity of a paired sender by its identifier, or <see langword="null" /> for one not paired.</param>
    /// <param name="data">The sealed bytes.</param>
    /// <returns>The package.</returns>
    /// <exception cref="InvalidDataException">It is not a package for this server from a paired one, or it was changed.</exception>
    SwarmMessage Open(ISwarmIdentity receiver, Func<string, SwarmPublicIdentity?> senderOf, ReadOnlySpan<byte> data);
}

/// <inheritdoc />
internal sealed class SwarmSealer : ISwarmSealer
{
    private readonly byte[] magic = Encoding.ASCII.GetBytes("ARSM1\n");

    private readonly int tagLength = 16;

    private readonly int signatureLength = 64;

    private readonly int maxHeader = 4096;

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Seal(ISwarmIdentity sender, SwarmPublicIdentity receiver, Guid id, SwarmMessageKind kind, ReadOnlySpan<byte> body, ReadOnlySpan<byte> blob, DateTimeOffset createdAt)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        SwarmEnvelopeHeader header = new() { Id = id, Kind = kind, From = sender.Public.Id, To = receiver.Id, CreatedAt = createdAt, Salt = Convert.ToBase64String(salt), Nonce = Convert.ToBase64String(nonce) };
        byte[] headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, options);
        byte[] plain = new byte[4 + body.Length + blob.Length];
        BinaryPrimitives.WriteInt32LittleEndian(plain, body.Length);
        body.CopyTo(plain.AsSpan(4));
        blob.CopyTo(plain.AsSpan(4 + body.Length));
        int prefix = magic.Length + 4 + headerBytes.Length + 4;
        byte[] result = new byte[prefix + plain.Length + tagLength + signatureLength];
        magic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(magic.Length), headerBytes.Length);
        headerBytes.CopyTo(result, magic.Length + 4);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(prefix - 4), plain.Length);

        using (AesGcm cipher = new(Key(sender.Share(receiver), salt, header), tagLength))
        {
            cipher.Encrypt(nonce, plain, result.AsSpan(prefix, plain.Length), result.AsSpan(prefix + plain.Length, tagLength), headerBytes);
        }

        sender.Sign(result.AsSpan(0, result.Length - signatureLength)).Span.CopyTo(result.AsSpan(result.Length - signatureLength));
        return result;
    }

    /// <inheritdoc />
    public SwarmEnvelopeHeader Peek(ReadOnlySpan<byte> data) => Parse(data).Header;

    /// <inheritdoc />
    public SwarmMessage Open(ISwarmIdentity receiver, Func<string, SwarmPublicIdentity?> senderOf, ReadOnlySpan<byte> data)
    {
        (SwarmEnvelopeHeader header, int headerStart, int headerLength, int cipherStart, int cipherLength) = Parse(data);

        if (header.To != receiver.Public.Id)
        {
            throw new InvalidDataException("The package is addressed to another server.");
        }

        SwarmPublicIdentity sender = senderOf(header.From) ?? throw new InvalidDataException("The package comes from a server that is not paired.");

        if (sender.Id != header.From || !Verify(sender, data[..^signatureLength], data[^signatureLength..]))
        {
            throw new InvalidDataException("The package is not signed by the server it says it comes from.");
        }

        byte[] plain = new byte[cipherLength];

        try
        {
            using AesGcm cipher = new(Key(receiver.Share(sender), Convert.FromBase64String(header.Salt), header), tagLength);
            cipher.Decrypt(Convert.FromBase64String(header.Nonce), data.Slice(cipherStart, cipherLength), data.Slice(cipherStart + cipherLength, tagLength), plain, data.Slice(headerStart, headerLength));
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidDataException("The package could not be decrypted.", exception);
        }

        int bodyLength = plain.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(plain) : -1;

        if (bodyLength < 0 || bodyLength > plain.Length - 4)
        {
            throw new InvalidDataException("The package is damaged.");
        }

        return new SwarmMessage { Id = header.Id, Kind = header.Kind, From = header.From, To = header.To, CreatedAt = header.CreatedAt, Body = plain.AsMemory(4, bodyLength), Blob = plain.AsMemory(4 + bodyLength) };
    }

    private (SwarmEnvelopeHeader Header, int HeaderStart, int HeaderLength, int CipherStart, int CipherLength) Parse(ReadOnlySpan<byte> data)
    {
        int headerStart = magic.Length + 4;

        if (data.Length < headerStart || !data[..magic.Length].SequenceEqual(magic))
        {
            throw new InvalidDataException("This is not a swarm package.");
        }

        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(data[magic.Length..]);

        if (headerLength is <= 0 || headerLength > maxHeader || data.Length < headerStart + headerLength + 4)
        {
            throw new InvalidDataException("The package is damaged.");
        }

        int cipherStart = headerStart + headerLength + 4;
        int cipherLength = BinaryPrimitives.ReadInt32LittleEndian(data[(headerStart + headerLength)..]);

        if (cipherLength < 0 || (long)cipherStart + cipherLength + tagLength + signatureLength != data.Length)
        {
            throw new InvalidDataException("The package is damaged.");
        }

        try
        {
            SwarmEnvelopeHeader header = JsonSerializer.Deserialize<SwarmEnvelopeHeader>(data.Slice(headerStart, headerLength), options) ?? throw new InvalidDataException("The package is damaged.");
            return (header, headerStart, headerLength, cipherStart, cipherLength);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The package is damaged.", exception);
        }
    }

    private byte[] Key(byte[] shared, byte[] salt, SwarmEnvelopeHeader header) => HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.UTF8.GetBytes($"auto-remote swarm {header.From} {header.To}"));

    private bool Verify(SwarmPublicIdentity sender, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(sender.SigningKey), out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return false;
        }
    }
}
