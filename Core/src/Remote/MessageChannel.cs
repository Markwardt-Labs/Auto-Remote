namespace Markwardt.AutoRemote;

/// <summary>
/// Newline-delimited text and JSON messages over a connected stream.
/// </summary>
public interface IMessageChannel
{
    /// <summary>
    /// Writes one raw text line.
    /// </summary>
    /// <param name="line">The line, without a terminator.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task SendLine(string line, CancellationToken cancellation);

    /// <summary>
    /// Reads one raw text line.
    /// </summary>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The line without its terminator, or <see langword="null" /> when the peer closed the stream.</returns>
    Task<string?> ReceiveLine(CancellationToken cancellation);

    /// <summary>
    /// Writes a message as one JSON line.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to write.</param>
    /// <param name="cancellation">Cancels the write.</param>
    Task Send<T>(T message, CancellationToken cancellation);

    /// <summary>
    /// Reads one JSON line as a message.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The message, or <see langword="null" /> when the peer closed the stream.</returns>
    /// <exception cref="InvalidDataException">The line is not a valid message.</exception>
    Task<T?> Receive<T>(CancellationToken cancellation)
        where T : class;

    /// <summary>
    /// Creates <see cref="IMessageChannel" /> instances.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Creates a channel over the given stream.
        /// </summary>
        /// <param name="stream">The connected stream; it stays open when the channel is dropped.</param>
        /// <returns>The channel.</returns>
        IMessageChannel Create(Stream stream);
    }

    /// <inheritdoc />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public IMessageChannel Create(Stream stream) => new MessageChannel(stream);
    }
}

/// <inheritdoc />
internal sealed class MessageChannel(Stream stream) : IMessageChannel
{
    private readonly StreamReader reader = new(stream, new UTF8Encoding(false), false, leaveOpen: true);

    private readonly JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <inheritdoc />
    public async Task SendLine(string line, CancellationToken cancellation)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancellation);
        await stream.FlushAsync(cancellation);
    }

    /// <inheritdoc />
    public async Task<string?> ReceiveLine(CancellationToken cancellation) => await reader.ReadLineAsync(cancellation);

    /// <inheritdoc />
    public Task Send<T>(T message, CancellationToken cancellation) => SendLine(JsonSerializer.Serialize(message, options), cancellation);

    /// <inheritdoc />
    public async Task<T?> Receive<T>(CancellationToken cancellation)
        where T : class
    {
        string? line = await ReceiveLine(cancellation);

        if (line is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(line, options);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Received an invalid message: {exception.Message}", exception);
        }
    }
}
