namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Accepts TLS connections, carrying plain TCP lines or WebSocket messages, and admits only those that supply the access key.
/// </summary>
internal interface IConnectionServer
{
    /// <summary>
    /// Raised with the bound TCP port and WebSocket port once the server is listening.
    /// </summary>
    event Action<int, int>? Listening;

    /// <summary>
    /// Raised with the SHA-256 fingerprint of the certificate the server presents, before it starts listening.
    /// </summary>
    event Action<string>? Certified;

    /// <summary>
    /// Listens on the configured TCP and WebSocket ports until cancelled.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="cancellation">Stops the server.</param>
    /// <exception cref="SocketException">The port could not be bound.</exception>
    Task Run(ServerConfig config, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ConnectionServer(IHandshake handshake, IMessageChannel.IFactory channels, IWebSocketUpgrade upgrade, ICertificateProvider certificates, IRequestHandler handler, IEventHub events, ILog log) : IConnectionServer
{
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public event Action<int, int>? Listening;

    /// <inheritdoc />
    public event Action<string>? Certified;

    /// <inheritdoc />
    public async Task Run(ServerConfig config, CancellationToken cancellation)
    {
        using X509Certificate2 certificate = await certificates.Load(config, cancellation);
        Certified?.Invoke(Convert.ToHexString(certificate.GetCertHash(HashAlgorithmName.SHA256)));
        TcpListener tcp = new(IPAddress.Any, config.Port);
        TcpListener web = new(IPAddress.Any, config.WebPort);

        try
        {
            tcp.Start();
            web.Start();
            Listening?.Invoke(((IPEndPoint)tcp.LocalEndpoint).Port, ((IPEndPoint)web.LocalEndpoint).Port);
            await Task.WhenAll(Accept(tcp, false, config.Key, certificate, cancellation), Accept(web, true, config.Key, certificate, cancellation));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            tcp.Stop();
            web.Stop();
        }
    }

    private async Task Accept(TcpListener listener, bool webSocket, string accessKey, X509Certificate2 certificate, CancellationToken cancellation)
    {
        while (true)
        {
            TcpClient client = await listener.AcceptTcpClientAsync(cancellation);
            _ = Serve(client, webSocket, accessKey, certificate, cancellation);
        }
    }

    private async Task Serve(TcpClient client, bool webSocket, string accessKey, X509Certificate2 certificate, CancellationToken cancellation)
    {
        string remote = $"{client.Client.RemoteEndPoint} ({(webSocket ? "WebSocket" : "TCP")})";
        log.Write($"Connection from {remote}");

        using (client)
        {
            try
            {
                using SslStream secure = new(client.GetStream(), false);
                using (CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    limit.CancelAfter(timeout);
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, limit.Token);
                }

                log.Write($"TLS established with {remote} ({secure.SslProtocol})");

                if ((webSocket ? await upgrade.Accept(secure, cancellation) : secure) is not { } stream)
                {
                    log.Write($"Refused {remote}: it sent no WebSocket upgrade in time or something other than one");
                    return;
                }

                HandshakeOutcome outcome = await handshake.Perform(stream, accessKey, cancellation);
                log.Write(outcome switch
                {
                    HandshakeOutcome.Accepted => $"Accepted {remote}",
                    HandshakeOutcome.WrongKey => $"Refused {remote}: wrong key",
                    HandshakeOutcome.TimedOut => $"Refused {remote}: no handshake line within {timeout.TotalSeconds:0} seconds, connection closed without a reply",
                    _ => $"Refused {remote}: the connection closed or sent something that is not a handshake line"
                });

                if (outcome == HandshakeOutcome.Accepted)
                {
                    DateTime connected = DateTime.UtcNow;
                    IMessageChannel channel = channels.Create(stream);
                    SemaphoreSlim writing = new(1, 1);
                    Channel<RemoteEvent> queue = Channel.CreateUnbounded<RemoteEvent>();
                    using IDisposable subscription = events.Subscribe(remoteEvent => queue.Writer.TryWrite(remoteEvent));
                    Task pushing = Push(channel, writing, queue.Reader, cancellation);

                    try
                    {
                        while (await channel.Receive<RemoteRequest>(cancellation) is { } request)
                        {
                            _ = Respond(channel, writing, client, request, cancellation);
                        }
                    }
                    finally
                    {
                        handler.Disconnect(client);
                        queue.Writer.Complete();
                        await pushing;
                        log.Write($"Disconnected {remote} after {(DateTime.UtcNow - connected).TotalSeconds:0} seconds");
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or InvalidDataException or AuthenticationException)
            {
                log.Write($"Connection {remote} ended: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    private async Task Push(IMessageChannel channel, SemaphoreSlim writing, ChannelReader<RemoteEvent> events, CancellationToken cancellation)
    {
        try
        {
            await foreach (RemoteEvent remoteEvent in events.ReadAllAsync(cancellation))
            {
                await Write(channel, writing, new RemoteResponse { Id = 0, Event = remoteEvent }, cancellation);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Respond(IMessageChannel channel, SemaphoreSlim writing, object owner, RemoteRequest request, CancellationToken cancellation)
    {
        RemoteResponse response;

        try
        {
            response = await handler.Handle(request, owner, cancellation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            log.Write($"Request {request.Command} failed: {exception}");
            response = new RemoteResponse { Id = request.Id, Error = "The server failed to handle the request." };
        }

        await Write(channel, writing, response, cancellation);
    }

    private async Task Write(IMessageChannel channel, SemaphoreSlim writing, RemoteResponse response, CancellationToken cancellation)
    {
        try
        {
            await writing.WaitAsync(cancellation);

            try
            {
                await channel.Send(response, cancellation);
            }
            finally
            {
                writing.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
