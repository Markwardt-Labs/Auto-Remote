namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Completes the WebSocket handshake for browsers and exposes the connection as lines.
/// </summary>
internal interface IWebSocketUpgrade
{
    /// <summary>
    /// Reads the client's HTTP upgrade request and completes the WebSocket handshake.
    /// </summary>
    /// <param name="stream">The secured connection to the client.</param>
    /// <param name="cancellation">Cancels the negotiation.</param>
    /// <returns>The stream the access key handshake continues on, or <see langword="null" /> if the client sent nothing in time or something other than an upgrade request.</returns>
    Task<Stream?> Accept(Stream stream, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class WebSocketUpgrade : IWebSocketUpgrade
{
    private readonly int maxRequestLength = 8192;

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

    private readonly string notice = "Auto Remote server. Your browser trusts this server's certificate, so the web client can connect now.";

    private readonly string magic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <inheritdoc />
    public async Task<Stream?> Accept(Stream stream, CancellationToken cancellation)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);

        try
        {
            string? key = ParseKey(await ReadRequest(stream, limit.Token));

            if (key is null)
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(notice)}\r\n\r\n{notice}"), limit.Token);
                return null;
            }

            string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + magic)));
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), limit.Token);
            return new WebSocketLineStream(WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) }));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return null;
        }
    }

    private string? ParseKey(string? request)
    {
        if (request is null)
        {
            return null;
        }

        string[] lines = request.Split("\r\n");

        if (!lines[0].StartsWith("GET ", StringComparison.Ordinal))
        {
            return null;
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');

            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        bool upgrade = headers.TryGetValue("Upgrade", out string? protocol) && protocol.Equals("websocket", StringComparison.OrdinalIgnoreCase);
        return upgrade && headers.TryGetValue("Sec-WebSocket-Key", out string? key) && key.Length > 0 ? key : null;
    }

    private async Task<string?> ReadRequest(Stream stream, CancellationToken cancellation)
    {
        List<byte> request = [];
        byte[] buffer = new byte[1];

        while (request.Count < maxRequestLength)
        {
            if (await stream.ReadAsync(buffer, cancellation) == 0)
            {
                return null;
            }

            request.Add(buffer[0]);

            if (request.Count >= 4 && request[^4] == '\r' && request[^3] == '\n' && request[^2] == '\r' && request[^1] == '\n')
            {
                return Encoding.ASCII.GetString([.. request]);
            }
        }

        return null;
    }
}
