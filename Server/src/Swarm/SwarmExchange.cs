namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Takes in the packages that arrive from paired servers, by network or drive alike, and puts together what is to be delivered to each. A task, result or cancellation is handled once however often it arrives, and acknowledged so its sender stops sending it.
/// </summary>
internal interface ISwarmExchange
{
    /// <summary>
    /// Takes in a package.
    /// </summary>
    /// <param name="data">The sealed bytes.</param>
    /// <param name="isNetwork">Whether it arrived over a connection, which means the sender is there now.</param>
    /// <returns>The identifier of the sender.</returns>
    /// <exception cref="InvalidDataException">It is not a usable package from a paired server.</exception>
    Task<string> Receive(ReadOnlyMemory<byte> data, bool isNetwork);

    /// <summary>
    /// Puts together what is to be delivered to a paired server: the packages waiting in its outbox, this server's profile, its acknowledgments and the progress of the tasks it gave.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <param name="cursor">What the connection already sent, so unchanged things are left out; <see langword="null" /> for a drive, which gets everything.</param>
    /// <returns>The packages, in the order to take them.</returns>
    Task<IReadOnlyList<SwarmParcel>> Mail(string peerId, SwarmCursor? cursor);

    /// <summary>
    /// Records that a paired server was just heard from over a connection, which a package that takes long to arrive also shows.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns>A task that completes once recorded.</returns>
    Task Heard(string peerId);
}

/// <inheritdoc />
internal sealed class SwarmExchange(ISwarmBook book, ISwarmCourier courier, ISwarmWorker worker, ISwarmCoordinator coordinator) : ISwarmExchange
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> senders = new();

    private readonly TimeSpan oldestTask = TimeSpan.FromDays(90);

    private readonly TimeSpan keptAcks = TimeSpan.FromDays(30);

    private readonly TimeSpan profileInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan seenResolution = TimeSpan.FromSeconds(10);

    private readonly TimeSpan resendInterval = TimeSpan.FromMinutes(1);

    private readonly int maxAcks = 500;

    /// <inheritdoc />
    public async Task<string> Receive(ReadOnlyMemory<byte> data, bool isNetwork)
    {
        SwarmMessage message = await courier.Open(data);
        SemaphoreSlim order = senders.GetOrAdd(message.From, _ => new SemaphoreSlim(1, 1));
        await order.WaitAsync();

        try
        {
            await Note(message, isNetwork);

            switch (message.Kind)
            {
                case SwarmMessageKind.Task or SwarmMessageKind.Result or SwarmMessageKind.Cancel:
                    await Handle(message, isNetwork);
                    break;
                case SwarmMessageKind.Profile:
                    await Profile(message.From, courier.Body<SwarmProfileBody>(message), message.CreatedAt);
                    break;
                case SwarmMessageKind.Ack:
                    foreach (Guid id in courier.Body<SwarmAckBody>(message).Ids.Where(id => courier.Withdraw(message.From, id)))
                    {
                        await coordinator.Delivered(message.From, id);
                        await worker.Delivered(id);
                    }

                    break;
                case SwarmMessageKind.Progress:
                    SwarmProgressBody progress = courier.Body<SwarmProgressBody>(message);
                    await coordinator.Progress(message.From, progress with { Tasks = [.. progress.Tasks.Where(task => task is { Messages: not null }).Take(1000)] });
                    break;
            }
        }
        finally
        {
            order.Release();
        }

        return message.From;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SwarmParcel>> Mail(string peerId, SwarmCursor? cursor)
    {
        List<SwarmParcel> parcels = [];

        foreach ((string name, string path) in courier.Outbox(peerId))
        {
            // One not acknowledged at once was not taken for now, and sending a large one again every round would fill the link.
            if (cursor is not null && cursor.Sent.TryGetValue(name, out DateTimeOffset at) && DateTimeOffset.UtcNow - at < resendInterval)
            {
                continue;
            }

            try
            {
                parcels.Add(new SwarmParcel(name, await File.ReadAllBytesAsync(path)));

                if (cursor is not null)
                {
                    cursor.Sent[name] = DateTimeOffset.UtcNow;
                }
            }
            catch (FileNotFoundException)
            {
            }
        }

        SwarmProfileBody profile = await worker.Profile(peerId);
        string described = JsonSerializer.Serialize(profile);

        if (cursor is null || cursor.Profile != described || DateTimeOffset.UtcNow - cursor.ProfileAt >= profileInterval)
        {
            parcels.Add(new SwarmParcel("profile.msg", await courier.Seal(peerId, SwarmMessageKind.Profile, profile)));

            if (cursor is not null)
            {
                cursor.Profile = described;
                cursor.ProfileAt = DateTimeOffset.UtcNow;
            }
        }

        List<Guid> acks = await book.Read(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId)?.Acks.Where(ack => DateTimeOffset.UtcNow - ack.At < keptAcks).Select(ack => ack.Id).ToList() ?? []);
        acks.RemoveAll(id => cursor?.Acknowledged.Contains(id) == true);

        if (acks.Count > 0)
        {
            parcels.Add(new SwarmParcel("ack.msg", await courier.Seal(peerId, SwarmMessageKind.Ack, new SwarmAckBody { Ids = acks })));
            cursor?.Acknowledged.UnionWith(acks);
        }

        if (await worker.Progress(peerId, cursor?.Progress) is { } progress)
        {
            parcels.Add(new SwarmParcel("progress.msg", await courier.Seal(peerId, SwarmMessageKind.Progress, progress)));
        }

        return parcels;
    }

    /// <inheritdoc />
    public async Task Heard(string peerId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (await book.Read(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } peer && (peer.LastSeen is not { } last || now - last > seenResolution)))
        {
            await book.Change(document =>
            {
                if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } peer)
                {
                    peer.LastSeen = now;
                }
            });
        }
    }

    private async Task Note(SwarmMessage message, bool isNetwork)
    {
        DateTimeOffset seen = isNetwork ? DateTimeOffset.UtcNow : message.CreatedAt;

        if (await book.Read(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == message.From) is { } peer && (peer.LastSeen is not { } last || seen - last > seenResolution || peer.Problem is not null)))
        {
            await book.Change(document =>
            {
                if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == message.From) is { } peer)
                {
                    peer.LastSeen = peer.LastSeen is { } last && last > seen ? last : seen;
                    peer.Problem = null;
                }
            });
        }
    }

    private async Task Handle(SwarmMessage message, bool isNetwork)
    {
        if (!await book.Read(document => document.Seen.ContainsKey(message.Id)))
        {
            try
            {
                switch (message.Kind)
                {
                    case SwarmMessageKind.Task:
                        await worker.Receive(message.From, courier.Body<SwarmTaskBody>(message), message.Blob, DateTimeOffset.UtcNow - message.CreatedAt >= oldestTask);
                        break;
                    case SwarmMessageKind.Result:
                        await coordinator.Receive(message.From, courier.Body<SwarmResultBody>(message), message.Blob);
                        break;
                    case SwarmMessageKind.Cancel:
                        await worker.Cancel(message.From, courier.Body<SwarmCancelBody>(message).TaskId, "The sender cancelled it.");
                        break;
                }
            }
            catch (InvalidDataException)
            {
                // A package that cannot be read never will be, so it is acknowledged like any other.
            }
            catch (RequestException exception) when (exception.IsTemporary)
            {
                // Unacknowledged, it comes again. A drive has to keep its copy, so that import is told.
                if (isNetwork)
                {
                    return;
                }

                throw;
            }
        }

        await book.Change(document =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            document.Seen[message.Id] = now;

            foreach (Guid old in document.Seen.Where(entry => now - entry.Value > oldestTask).Select(entry => entry.Key).ToList())
            {
                document.Seen.Remove(old);
            }

            if (document.Peers.FirstOrDefault(peer => peer.Identity.Id == message.From) is { } peer)
            {
                peer.Acks.RemoveAll(ack => ack.Id == message.Id || now - ack.At > keptAcks);
                peer.Acks.Add(new SwarmAcked { Id = message.Id, At = now });
                peer.Acks.RemoveRange(0, Math.Max(0, peer.Acks.Count - maxAcks));
            }
        }, false);
    }

    private async Task Profile(string peerId, SwarmProfileBody profile, DateTimeOffset createdAt)
    {
        // A paired server is not trusted to send well formed lists.
        profile = profile with { Capabilities = [.. profile.Capabilities.Where(capability => capability is { Model: not null }).Take(8)] };
        bool changed = await book.Read(document => document.Peers.FirstOrDefault(peer => peer.Identity.Id == peerId) is { } peer && (peer.ProfileAt is not { } at || createdAt > at));

        if (!changed)
        {
            return;
        }

        bool differs = await book.Read(document => JsonSerializer.Serialize(document.Peers.First(peer => peer.Identity.Id == peerId).Profile) != JsonSerializer.Serialize(profile));
        await book.Change(document =>
        {
            SwarmPeerRecord peer = document.Peers.First(peer => peer.Identity.Id == peerId);
            peer.Profile = profile;
            peer.ProfileAt = createdAt;
        }, differs);

        // Giving out work can take a while, which must not hold up the connection the profile arrived on.
        _ = Task.Run(coordinator.Dispatch);
    }
}
