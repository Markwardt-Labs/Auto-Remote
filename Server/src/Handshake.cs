namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Authenticates a newly connected client by its access key.
/// </summary>
internal interface IHandshake
{
    /// <summary>
    /// Reads the client's <see cref="HandshakeRequest" /> line and replies with a <see cref="HandshakeResponse" />.
    /// </summary>
    /// <param name="stream">The connection to the client.</param>
    /// <param name="accessKey">The key the client must supply.</param>
    /// <param name="cancellation">Cancels the handshake.</param>
    /// <returns>How the handshake ended; the client is answered in every case except <see cref="HandshakeOutcome.TimedOut" />.</returns>
    Task<HandshakeOutcome> Perform(Stream stream, string accessKey, CancellationToken cancellation);
}

/// <summary>
/// How a <see cref="IHandshake" /> ended.
/// </summary>
internal enum HandshakeOutcome
{
    /// <summary>
    /// The client supplied the correct key.
    /// </summary>
    Accepted,

    /// <summary>
    /// The client supplied a different key.
    /// </summary>
    WrongKey,

    /// <summary>
    /// The client closed the connection, sent more than the allowed length or sent something that is not a handshake line.
    /// </summary>
    Invalid,

    /// <summary>
    /// No complete handshake line arrived in time.
    /// </summary>
    TimedOut
}

/// <inheritdoc />
internal sealed class Handshake : IHandshake
{
    private readonly int maxLength = 1024;

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<HandshakeOutcome> Perform(Stream stream, string accessKey, CancellationToken cancellation)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);

        HandshakeOutcome outcome;

        try
        {
            outcome = Parse(await ReadLine(stream, limit.Token)) is not { } supplied ? HandshakeOutcome.Invalid : CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(accessKey)) ? HandshakeOutcome.Accepted : HandshakeOutcome.WrongKey;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return HandshakeOutcome.TimedOut;
        }

        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new HandshakeResponse { Accepted = outcome == HandshakeOutcome.Accepted }, options) + "\n"), cancellation);
        await stream.FlushAsync(cancellation);
        return outcome;
    }

    private string? Parse(byte[]? line)
    {
        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HandshakeRequest>(line, options)?.AccessKey;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]?> ReadLine(Stream stream, CancellationToken cancellation)
    {
        List<byte> line = [];
        byte[] buffer = new byte[1];

        while (line.Count <= maxLength)
        {
            if (await stream.ReadAsync(buffer, cancellation) == 0)
            {
                return null;
            }

            if (buffer[0] == (byte)'\n')
            {
                return [.. line.Where(value => value != (byte)'\r')];
            }

            line.Add(buffer[0]);
        }

        return null;
    }
}
