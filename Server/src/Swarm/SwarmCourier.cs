namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The packages of this server: it seals them, keeps those that must arrive in an outbox per paired server until they are acknowledged, and opens what arrives.
/// </summary>
internal interface ISwarmCourier
{
    /// <summary>
    /// Seals a package that must arrive into the outbox of a paired server.
    /// </summary>
    /// <param name="peerId">The receiver.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="body">Its content.</param>
    /// <param name="blob">Binary data it carries.</param>
    /// <returns>Its identifier, which the acknowledgment names.</returns>
    /// <exception cref="RequestException">The receiver is not paired.</exception>
    Task<Guid> Post(string peerId, SwarmMessageKind kind, object body, ReadOnlyMemory<byte> blob);

    /// <summary>
    /// Seals a package that is made afresh whenever it is sent, such as a profile.
    /// </summary>
    /// <param name="peerId">The receiver.</param>
    /// <param name="kind">What it is.</param>
    /// <param name="body">Its content.</param>
    /// <returns>The sealed bytes.</returns>
    /// <exception cref="RequestException">The receiver is not paired.</exception>
    Task<ReadOnlyMemory<byte>> Seal(string peerId, SwarmMessageKind kind, object body);

    /// <summary>
    /// Lists the packages waiting for a paired server, the oldest first.
    /// </summary>
    /// <param name="peerId">The receiver.</param>
    /// <returns>The name and path of each.</returns>
    IReadOnlyList<(string Name, string Path)> Outbox(string peerId);

    /// <summary>
    /// Takes a package out of the outbox of a paired server, once it arrived or is no longer wanted.
    /// </summary>
    /// <param name="peerId">The receiver.</param>
    /// <param name="id">The package.</param>
    /// <returns><see langword="true" /> if it was there.</returns>
    bool Withdraw(string peerId, Guid id);

    /// <summary>
    /// Tells whether a package still waits in the outbox of a paired server to be acknowledged.
    /// </summary>
    /// <param name="peerId">The receiver.</param>
    /// <param name="id">The package.</param>
    /// <returns><see langword="true" /> if it waits.</returns>
    bool Waits(string peerId, Guid id);

    /// <summary>
    /// Opens a package that arrived.
    /// </summary>
    /// <param name="data">The sealed bytes.</param>
    /// <returns>The package.</returns>
    /// <exception cref="InvalidDataException">It is not a package for this server from a paired one.</exception>
    Task<SwarmMessage> Open(ReadOnlyMemory<byte> data);

    /// <summary>
    /// Reads the content of an opened package.
    /// </summary>
    /// <typeparam name="T">The content.</typeparam>
    /// <param name="message">The package.</param>
    /// <returns>The content.</returns>
    /// <exception cref="InvalidDataException">The content is not what the kind says.</exception>
    T Body<T>(SwarmMessage message);

    /// <summary>
    /// Deletes the outbox of a paired server.
    /// </summary>
    /// <param name="peerId">The server.</param>
    void Remove(string peerId);
}

/// <inheritdoc />
internal sealed class SwarmCourier(ISwarmBook book, ISwarmSealer sealer) : ISwarmCourier
{
    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true };

    private long last;

    /// <inheritdoc />
    public async Task<Guid> Post(string peerId, SwarmMessageKind kind, object body, ReadOnlyMemory<byte> blob)
    {
        SwarmPublicIdentity receiver = await Receiver(peerId);
        Guid id = Guid.NewGuid();
        ReadOnlyMemory<byte> sealedBytes = sealer.Seal(book.Identity, receiver, id, kind, JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), options), blob.Span, DateTimeOffset.UtcNow);
        string folder = OutboxFolder(peerId);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Sequence():D19}-{id:N}.msg");
        await File.WriteAllBytesAsync(path + ".tmp", sealedBytes);
        File.Move(path + ".tmp", path);
        return id;
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> Seal(string peerId, SwarmMessageKind kind, object body) => sealer.Seal(book.Identity, await Receiver(peerId), Guid.NewGuid(), kind, JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), options), [], DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public IReadOnlyList<(string Name, string Path)> Outbox(string peerId)
    {
        string folder = OutboxFolder(peerId);
        return Directory.Exists(folder) ? [.. Directory.GetFiles(folder, "*.msg").Order(StringComparer.Ordinal).Select(path => (System.IO.Path.GetFileName(path), path))] : [];
    }

    /// <inheritdoc />
    public bool Waits(string peerId, Guid id) => Outbox(peerId).Any(package => package.Name.EndsWith($"-{id:N}.msg", StringComparison.Ordinal));

    /// <inheritdoc />
    public bool Withdraw(string peerId, Guid id)
    {
        bool found = false;

        foreach ((string name, string path) in Outbox(peerId))
        {
            if (name.EndsWith($"-{id:N}.msg", StringComparison.Ordinal))
            {
                File.Delete(path);
                found = true;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<SwarmMessage> Open(ReadOnlyMemory<byte> data)
    {
        Dictionary<string, SwarmPublicIdentity> paired = await book.Read(document => document.Peers.ToDictionary(peer => peer.Identity.Id, peer => peer.Identity));
        return sealer.Open(book.Identity, id => paired.GetValueOrDefault(id), data.Span);
    }

    /// <inheritdoc />
    public T Body<T>(SwarmMessage message)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(message.Body.Span, options) ?? throw new InvalidDataException("The package is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The content of the package is not valid: {exception.Message}", exception);
        }
    }

    /// <inheritdoc />
    public void Remove(string peerId)
    {
        string folder = OutboxFolder(peerId);

        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }

    private async Task<SwarmPublicIdentity> Receiver(string peerId) => await book.Read(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId)?.Identity) ?? throw new RequestException("That server is not paired.");

    private string OutboxFolder(string peerId) => Path.Combine(book.Folder, "outbox", peerId.All(char.IsAsciiHexDigit) ? peerId : throw new RequestException("That server is not paired."));

    // Names sort in the order packages were posted, even within one clock tick, so a cancellation never overtakes its task.
    private long Sequence()
    {
        long now = DateTime.UtcNow.Ticks;
        long previous;
        long next;

        do
        {
            previous = Interlocked.Read(ref last);
            next = Math.Max(now, previous + 1);
        }
        while (Interlocked.CompareExchange(ref last, next, previous) != previous);

        return next;
    }
}
