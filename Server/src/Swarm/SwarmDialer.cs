namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Opens connections to other servers.
/// </summary>
internal interface ISwarmDialer
{
    /// <summary>
    /// Connects to a server as a peer, which gets no access to anything but the exchange of signed packages.
    /// </summary>
    /// <param name="address">The <c>host:port</c> of the server.</param>
    /// <param name="cancellation">Cancels the connecting.</param>
    /// <returns>The connection, which disposing closes.</returns>
    /// <exception cref="IOException">The server cannot be reached or does not take peers.</exception>
    Task<(IMessageChannel Channel, IDisposable Connection)> Connect(string address, CancellationToken cancellation);

    /// <summary>
    /// Reads a <c>host:port</c>; the port is the server's usual one when left out.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The address of the connection.</returns>
    /// <exception cref="RequestException">It is not an address.</exception>
    Uri Parse(string address);
}

/// <inheritdoc />
internal sealed class SwarmDialer(IMessageChannel.IFactory channels) : ISwarmDialer
{
    private readonly int defaultPort = 52679;

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(10);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <inheritdoc />
    public async Task<(IMessageChannel Channel, IDisposable Connection)> Connect(string address, CancellationToken cancellation)
    {
        Uri target = Parse(address);
        ClientWebSocket socket = new();

        try
        {
            // Trust comes from the signed and encrypted packages and the pairing proofs, not from the certificate, so any certificate is accepted.
            socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            limit.CancelAfter(timeout);
            await socket.ConnectAsync(target, limit.Token);
            WebSocketLineStream stream = new(socket) { MaxMessageLength = 6 * 1024 * 1024 };
            IMessageChannel channel = channels.Create(stream);
            await channel.SendLine(JsonSerializer.Serialize(new HandshakeRequest { AccessKey = string.Empty, Peer = true }, options), limit.Token);
            string? answer = await channel.ReceiveLine(limit.Token);

            if (answer is null || JsonSerializer.Deserialize<HandshakeResponse>(answer, options) is not { Accepted: true })
            {
                throw new IOException("The other server does not take swarm connections.");
            }

            return (channel, stream);
        }
        catch (Exception exception) when (exception is WebSocketException or JsonException or InvalidDataException or HttpRequestException)
        {
            socket.Dispose();
            throw new IOException($"The server could not be reached: {exception.Message}", exception);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException("The server did not answer in time.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Uri Parse(string address)
    {
        string text = address.Trim();
        bool hasPort = Regex.IsMatch(text, @"(\]|^[^:]+):\d+$");

        if (text.Length == 0 || text.Contains('/', StringComparison.Ordinal) || !Uri.TryCreate($"wss://{text}{(hasPort ? string.Empty : $":{defaultPort}")}", UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0 || uri.Port is < 1 or > 65535 || uri.UserInfo.Length > 0)
        {
            throw new RequestException("Enter the address of the server as host:port.");
        }

        return uri;
    }
}
