namespace Markwardt.AutoRemote.Server;

/// <summary>
/// The network delivery of the swarm: this server connects to the paired servers it has an address for, in the networked modes, and accepts the connections of those that connect to it. Each connection starts with a signed hello, then takes turns: the connecting side sends its mail, the other takes it in and answers with its own. Pairing with a code happens over the same port.
/// </summary>
internal interface ISwarmLink : IPeerListener
{
    /// <summary>
    /// Gets the pairing code that works now, or <see langword="null" />.
    /// </summary>
    SwarmPairingCode? Code { get; }

    /// <summary>
    /// Makes a new pairing code, replacing any earlier one.
    /// </summary>
    /// <returns>The code.</returns>
    SwarmPairingCode CreateCode();

    /// <summary>
    /// Pairs with the server that made a code.
    /// </summary>
    /// <param name="address">Its <c>host:port</c>, which later connections go to.</param>
    /// <param name="code">The code.</param>
    /// <param name="cancellation">Cancels the pairing.</param>
    /// <returns>The name of the paired server.</returns>
    /// <exception cref="RequestException">It cannot be reached or refused the code.</exception>
    Task<string> Pair(string address, string code, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a connection with a paired server is open.
    /// </summary>
    /// <param name="peerId">The server.</param>
    /// <returns><see langword="true" /> if one is.</returns>
    bool IsConnected(string peerId);

    /// <summary>
    /// Keeps connections open to the paired servers this server connects to, until cancelled.
    /// </summary>
    /// <param name="cancellation">Ends every connection.</param>
    /// <returns>A task that completes once cancelled.</returns>
    Task Run(CancellationToken cancellation);

    /// <summary>
    /// Ends the connections with a paired server, as a change of its settings needs; the next ones follow the new settings.
    /// </summary>
    /// <param name="peerId">The server.</param>
    void Disconnect(string peerId);
}

/// <inheritdoc />
internal sealed class SwarmLink(ISwarmBook book, ISwarmCourier courier, ISwarmExchange exchange, ISwarmDialer dialer, ILog log) : ISwarmLink
{
    private readonly ConcurrentDictionary<string, (string Settings, CancellationTokenSource Stop, Task Session)> dialing = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, CancellationTokenSource>> connected = new();

    private readonly ConcurrentDictionary<string, DateTimeOffset> nonces = new();

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset Next)> retries = new();

    private readonly Lock pairing = new();

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeSpan codeLifetime = TimeSpan.FromMinutes(15);

    private readonly TimeSpan clockSkew = TimeSpan.FromMinutes(15);

    private readonly TimeSpan idle = TimeSpan.FromMinutes(2);

    // The other side answers only once it has taken what was sent to it, which can include fetching code.
    private readonly TimeSpan replyPatience = TimeSpan.FromMinutes(10);

    private readonly int chunk = 3 * 1024 * 1024;

    private readonly long maxPackage = 300L * 1024 * 1024;

    private readonly int strangerLength = 16 * 1024;

    private readonly int peerLength = 6 * 1024 * 1024;

    private readonly int maxStrangers = 8;

    private readonly int maxStrangersPerAddress = 3;

    private readonly int maxSessionsPerPeer = 2;

    private readonly Dictionary<string, int> strangers = [];

    private SwarmPairingCode? active;

    private int guesses;

    /// <summary>
    /// Gets how long a connection waits between rounds while tasks are under way with the other server.
    /// </summary>
    internal TimeSpan BusyInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets how long a connection waits between rounds otherwise.
    /// </summary>
    internal TimeSpan QuietInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public SwarmPairingCode? Code
    {
        get
        {
            lock (pairing)
            {
                return active is { } code && code.ExpiresAt > DateTimeOffset.UtcNow ? code : null;
            }
        }
    }

    /// <inheritdoc />
    public SwarmPairingCode CreateCode()
    {
        string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        string characters = new([.. Enumerable.Range(0, 24).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)])]);

        lock (pairing)
        {
            guesses = 0;
            active = new SwarmPairingCode { Code = string.Join('-', characters.Chunk(4).Select(group => new string(group))), ExpiresAt = DateTimeOffset.UtcNow + codeLifetime };
            return active;
        }
    }

    /// <inheritdoc />
    public async Task<string> Pair(string address, string code, CancellationToken cancellation)
    {
        byte[] key = Key(code);

        try
        {
            (IMessageChannel channel, IDisposable connection) = await dialer.Connect(address, cancellation);

            using (connection)
            {
                string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
                string name = await book.Read(document => document.Server.Name);
                SwarmPublicIdentity me = book.Identity.Public;
                await channel.Send(new SwarmFrame { Type = "pair", Name = name, Identity = me, Nonce = nonce, Proof = Proof(key, "join", nonce, me.SigningKey, me.ExchangeKey, name) }, cancellation);
                SwarmFrame answer = await Receive(channel, cancellation) ?? throw new RequestException("The other server closed the connection.");

                if (answer is not { Type: "paired", Identity: { } theirs, Name: { } theirName, Proof: { } proof })
                {
                    throw new RequestException(answer.Error ?? "The other server did not accept the code.");
                }

                if (!Matches(proof, Proof(key, "accept", nonce, theirs.SigningKey, theirs.ExchangeKey, theirName, me.SigningKey, me.ExchangeKey)))
                {
                    throw new RequestException("The other server could not prove it knows the code, so something between the two may be tampering.");
                }

                await book.Pair(theirs, theirName, SwarmMode.Direct, address.Trim());
                return theirName;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            throw new RequestException(exception.Message);
        }
    }

    /// <inheritdoc />
    public bool IsConnected(string peerId) => connected.TryGetValue(peerId, out ConcurrentDictionary<Guid, CancellationTokenSource>? sessions) && !sessions.IsEmpty;

    /// <inheritdoc />
    public void Disconnect(string peerId)
    {
        retries.TryRemove(peerId, out _);

        Retire(peerId);

        if (connected.TryGetValue(peerId, out ConcurrentDictionary<Guid, CancellationTokenSource>? sessions))
        {
            foreach (CancellationTokenSource stop in sessions.Values)
            {
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task Run(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                Dictionary<string, string> wanted = await book.Read(document => document.Peers.Where(peer => peer.Address.Length > 0 && peer.Mode != SwarmMode.AirGapped).ToDictionary(peer => peer.Identity.Id, peer => $"{peer.Mode} {peer.Address}"));

                foreach ((string peer, (string settings, CancellationTokenSource stop, _)) in dialing.ToList())
                {
                    if (!wanted.TryGetValue(peer, out string? current) || current != settings)
                    {
                        Retire(peer);
                    }
                }

                foreach ((string peer, string settings) in wanted)
                {
                    bool running = dialing.TryGetValue(peer, out (string Settings, CancellationTokenSource Stop, Task Session) session) && !session.Session.IsCompleted;

                    if (!running && (!retries.TryGetValue(peer, out (int Failures, DateTimeOffset Next) retry) || retry.Next <= DateTimeOffset.UtcNow))
                    {
                        Retire(peer);
                        CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        string address = settings[(settings.IndexOf(' ') + 1)..];
                        dialing[peer] = (settings, stop, Task.Run(() => Dial(peer, address, stop.Token), CancellationToken.None));
                    }
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            foreach ((_, CancellationTokenSource stop, _) in dialing.Values)
            {
                await stop.CancelAsync();
            }

            await Task.WhenAll(dialing.Values.Select(session => session.Session));
        }
    }

    /// <inheritdoc />
    public async Task Serve(IMessageChannel channel, Action<int> limit, string remote, CancellationToken cancellation)
    {
        // Until it proves who it is, a connection gets little memory, and few such connections are held, apart from those of paired servers.
        limit(strangerLength);
        string address = remote.LastIndexOf(':') is > 0 and int colon ? remote[..colon] : remote;

        if (!Admit(address))
        {
            return;
        }

        bool waiting = true;

        void Release()
        {
            if (waiting)
            {
                waiting = false;
                Leave(address);
            }
        }

        try
        {
            using CancellationTokenSource patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            patience.CancelAfter(TimeSpan.FromSeconds(10));
            SwarmFrame? first = await Receive(channel, patience.Token);

            if (first is { Type: "pair" })
            {
                await Accept(channel, first, remote, patience.Token);
            }
            else if (first is { Type: "hello", Data: { } data })
            {
                string peer = await Greet(Convert.FromBase64String(data), null);
                limit(peerLength);
                Release();
                await Answer(channel, peer, cancellation);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or FormatException or JsonException or RequestException)
        {
            log.Write($"Swarm connection from {remote} ended: {exception.Message}");
        }
        finally
        {
            Release();
        }
    }

    private bool Admit(string address)
    {
        lock (strangers)
        {
            if (strangers.Values.Sum() >= maxStrangers || strangers.GetValueOrDefault(address) >= maxStrangersPerAddress)
            {
                return false;
            }

            strangers[address] = strangers.GetValueOrDefault(address) + 1;
            return true;
        }
    }

    private void Leave(string address)
    {
        lock (strangers)
        {
            if (--strangers[address] <= 0)
            {
                strangers.Remove(address);
            }
        }
    }

    private async Task Accept(IMessageChannel channel, SwarmFrame frame, string remote, CancellationToken cancellation)
    {
        if (frame is not { Identity: { } theirs, Name: { } name, Nonce: { } nonce, Proof: { } proof })
        {
            return;
        }

        byte[]? key = null;

        lock (pairing)
        {
            if (active is { } code && code.ExpiresAt > DateTimeOffset.UtcNow)
            {
                byte[] candidate = Key(code.Code);

                if (Matches(proof, Proof(candidate, "join", nonce, theirs.SigningKey, theirs.ExchangeKey, name)))
                {
                    key = candidate;
                    active = null;
                }
                else if (++guesses >= 5)
                {
                    active = null;
                }
            }
        }

        if (key is null)
        {
            log.Write($"Refused a swarm pairing from {remote}: wrong or expired code");
            await channel.Send(new SwarmFrame { Type = "error", Error = "The code is wrong or no longer works; make a new one." }, cancellation);
            return;
        }

        await book.Pair(theirs, name, SwarmMode.Direct, string.Empty);
        SwarmPublicIdentity me = book.Identity.Public;
        string mine = await book.Read(document => document.Server.Name);
        await channel.Send(new SwarmFrame { Type = "paired", Identity = me, Name = mine, Proof = Proof(key, "accept", nonce, me.SigningKey, me.ExchangeKey, mine, theirs.SigningKey, theirs.ExchangeKey) }, cancellation);
        log.Write($"Paired with {name} from {remote}");
    }

    private async Task Answer(IMessageChannel channel, string peer, CancellationToken cancellation)
    {
        if (await book.Read(document => document.Peers.First(entry => entry.Identity.Id == peer).Mode == SwarmMode.AirGapped))
        {
            await channel.Send(new SwarmFrame { Type = "error", Error = "This server is set to reach you by drive only." }, cancellation);
            return;
        }

        await channel.Send(new SwarmFrame { Type = "hello", Data = Convert.ToBase64String((await Hello(peer)).Span) }, cancellation);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Guid session = Open(peer, stop);

        try
        {
            SwarmCursor cursor = new();

            while (true)
            {
                if (!await Take(channel, peer, stop.Token))
                {
                    return;
                }

                await Deliver(channel, peer, await exchange.Mail(peer, cursor), stop.Token);
            }
        }
        finally
        {
            Close(peer, session);
        }
    }

    // A source linked to the server's stays referenced by it until disposed, which has to wait for the dial that uses it.
    private void Retire(string peer)
    {
        if (dialing.TryRemove(peer, out (string Settings, CancellationTokenSource Stop, Task Session) retired))
        {
            retired.Stop.Cancel();
            _ = retired.Session.ContinueWith(_ => retired.Stop.Dispose(), TaskScheduler.Default);
        }
    }

    private async Task Dial(string peer, string address, CancellationToken cancellation)
    {
        try
        {
            (IMessageChannel channel, IDisposable connection) = await dialer.Connect(address, cancellation);

            using (connection)
            {
                await channel.Send(new SwarmFrame { Type = "hello", Data = Convert.ToBase64String((await Hello(peer)).Span) }, cancellation);
                SwarmFrame answer = await Receive(channel, cancellation) ?? throw new IOException("The other server closed the connection; it may not be paired with this one any more.");

                if (answer is not { Type: "hello", Data: { } data })
                {
                    throw new IOException(answer.Error ?? "The other server refused the connection.");
                }

                await Greet(Convert.FromBase64String(data), peer);
                using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                Guid session = Open(peer, stop);

                try
                {
                    SwarmCursor cursor = new();

                    while (!stop.IsCancellationRequested)
                    {
                        await Deliver(channel, peer, await exchange.Mail(peer, cursor), stop.Token);

                        if (!await Take(channel, peer, stop.Token))
                        {
                            throw new IOException("The other server closed the connection.");
                        }

                        retries.TryRemove(peer, out _);
                        await Task.Delay(await Busy(peer) ? BusyInterval : QuietInterval, stop.Token);
                    }
                }
                finally
                {
                    Close(peer, session);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or OperationCanceledException or FormatException or JsonException or RequestException or ObjectDisposedException or UnauthorizedAccessException)
        {
            int failures = retries.TryGetValue(peer, out (int Failures, DateTimeOffset Next) before) ? before.Failures + 1 : 1;
            retries[peer] = (failures, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(60, 2 * Math.Pow(2, Math.Min(failures, 5)))));
            await Note(peer, exception.Message);
        }
    }

    private async Task<bool> Busy(string peer) => await book.Read(document => document.Outgoing.Any(task => task.PeerId == peer && task.State is SwarmTaskState.InTransit or SwarmTaskState.Queued or SwarmTaskState.Running) || document.Incoming.Any(task => task.PeerId == peer && task.State is SwarmTaskState.Queued or SwarmTaskState.Running));

    private async Task Note(string peer, string problem)
    {
        if (await book.Read(document => document.Peers.FirstOrDefault(entry => entry.Identity.Id == peer) is { } entry && entry.Problem != problem))
        {
            await book.Change(document =>
            {
                if (document.Peers.FirstOrDefault(entry => entry.Identity.Id == peer) is { } entry)
                {
                    entry.Problem = problem;
                }
            });
        }
    }

    private Guid Open(string peer, CancellationTokenSource stop)
    {
        Guid session = Guid.NewGuid();
        ConcurrentDictionary<Guid, CancellationTokenSource> sessions = connected.GetOrAdd(peer, _ => new());

        // A peer keeps one connection of its own and one this server opened; more are left over from broken ones.
        foreach (CancellationTokenSource old in sessions.Values.Skip(maxSessionsPerPeer - 1))
        {
            try
            {
                old.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        sessions[session] = stop;
        book.Announce();
        return session;
    }

    private void Close(string peer, Guid session)
    {
        if (connected.TryGetValue(peer, out ConcurrentDictionary<Guid, CancellationTokenSource>? sessions))
        {
            sessions.TryRemove(session, out _);
        }

        book.Announce();
    }

    private Task<ReadOnlyMemory<byte>> Hello(string peer) => courier.Seal(peer, SwarmMessageKind.Hello, new SwarmHelloBody { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)) });

    private async Task<string> Greet(ReadOnlyMemory<byte> data, string? expected)
    {
        SwarmMessage message = await courier.Open(data);

        if (message.Kind != SwarmMessageKind.Hello || expected is not null && message.From != expected)
        {
            throw new InvalidDataException("The other server did not introduce itself.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        if ((now - message.CreatedAt).Duration() > clockSkew)
        {
            throw new InvalidDataException("The clocks of the two servers differ by more than 15 minutes.");
        }

        foreach (string old in nonces.Where(entry => now - entry.Value > clockSkew * 2).Select(entry => entry.Key).ToList())
        {
            nonces.TryRemove(old, out _);
        }

        if (!nonces.TryAdd($"{message.From} {courier.Body<SwarmHelloBody>(message).Nonce}", now))
        {
            throw new InvalidDataException("The greeting was used before.");
        }

        return message.From;
    }

    private async Task Deliver(IMessageChannel channel, string peer, IReadOnlyList<SwarmParcel> mail, CancellationToken cancellation)
    {
        foreach (SwarmParcel parcel in mail)
        {
            int count = Math.Max(1, (parcel.Data.Length + chunk - 1) / chunk);

            for (int index = 0; index < count; index++)
            {
                ReadOnlyMemory<byte> part = parcel.Data.Slice(index * chunk, Math.Min(chunk, parcel.Data.Length - index * chunk));
                await channel.Send(new SwarmFrame { Type = "part", Index = index, Count = count, Data = Convert.ToBase64String(part.Span) }, cancellation);
                await exchange.Heard(peer);
            }
        }

        await channel.Send(new SwarmFrame { Type = "end" }, cancellation);
    }

    private async Task<bool> Take(IMessageChannel channel, string peer, CancellationToken cancellation)
    {
        MemoryStream? package = null;
        int expected = 0;
        TimeSpan patience = replyPatience;

        // Only silence ends the connection: a large package over a slow link, and taking what arrived, may take as long as they need.
        while (await Receive(channel, patience, cancellation) is { } frame)
        {
            patience = idle;
            await exchange.Heard(peer);

            if (frame.Type == "end")
            {
                return true;
            }

            if (frame is not { Type: "part", Data: { } data } || frame.Index != expected || frame.Count < 1 || frame.Index >= frame.Count)
            {
                throw new InvalidDataException(frame.Error ?? "The other server sent something out of turn.");
            }

            package ??= new MemoryStream();
            byte[] bytes = Convert.FromBase64String(data);

            if (package.Length + bytes.Length > maxPackage)
            {
                throw new InvalidDataException("A package is too large.");
            }

            package.Write(bytes);
            expected++;

            if (expected == frame.Count)
            {
                byte[] whole = package.ToArray();
                package = null;
                expected = 0;

                if (await exchange.Receive(whole, true) != peer)
                {
                    throw new InvalidDataException("A package came from another server than the connection.");
                }
            }
        }

        return false;
    }

    private Task<SwarmFrame?> Receive(IMessageChannel channel, CancellationToken cancellation) => channel.Receive<SwarmFrame>(cancellation);

    private async Task<SwarmFrame?> Receive(IMessageChannel channel, TimeSpan patience, CancellationToken cancellation)
    {
        using CancellationTokenSource silence = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        silence.CancelAfter(patience);
        return await Receive(channel, silence.Token);
    }

    private byte[] Key(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code)));

    private string Normalize(string code) => new string([.. code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit)]).Replace('O', '0').Replace('I', '1').Replace('L', '1');

    private string Proof(byte[] key, params string[] parts) => Convert.ToBase64String(HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(parts, options)));

    private bool Matches(string first, string second) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
}
