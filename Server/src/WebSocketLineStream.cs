namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Presents a server-side WebSocket as a byte stream of newline-delimited lines: every received message becomes one line and every write is sent as one text message.
/// </summary>
internal sealed class WebSocketLineStream(WebSocket socket) : Stream
{
    private readonly int maxMessageLength = 4 * 1024 * 1024;

    private readonly List<byte> pending = [];

    private int position;

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellation = default)
    {
        while (position >= pending.Count)
        {
            pending.Clear();
            position = 0;

            if (!await ReceiveMessage(cancellation))
            {
                return 0;
            }
        }

        int count = Math.Min(buffer.Length, pending.Count - position);
        CollectionsMarshal.AsSpan(pending).Slice(position, count).CopyTo(buffer.Span);
        position += count;
        return count;
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellation = default) => await socket.SendAsync(buffer, WebSocketMessageType.Text, true, cancellation);

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellation) => Task.CompletedTask;

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            socket.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<bool> ReceiveMessage(CancellationToken cancellation)
    {
        byte[] chunk = new byte[4096];

        while (true)
        {
            WebSocketReceiveResult result;

            try
            {
                result = await socket.ReceiveAsync(chunk, cancellation);
            }
            catch (WebSocketException exception)
            {
                throw new IOException(exception.Message, exception);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return false;
            }

            pending.AddRange(chunk.AsSpan(0, result.Count));

            if (pending.Count > maxMessageLength)
            {
                throw new InvalidDataException("The message is too long.");
            }

            if (result.EndOfMessage)
            {
                if (pending.Count == 0 || pending[^1] != (byte)'\n')
                {
                    pending.Add((byte)'\n');
                }

                return true;
            }
        }
    }
}
