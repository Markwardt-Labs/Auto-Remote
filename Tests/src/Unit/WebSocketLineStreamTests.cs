namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WebSocketLineStreamTests
{
    private static Mock<WebSocket> Socket(params (string text, bool end)[] frames)
    {
        Mock<WebSocket> socket = new();
        Queue<(string text, bool end)> queue = new(frames);
        socket.Setup(item => item.ReceiveAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync((ArraySegment<byte> buffer, CancellationToken _) =>
        {
            if (queue.Count == 0)
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            (string text, bool end) = queue.Dequeue();
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            bytes.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(bytes.Length, WebSocketMessageType.Text, end);
        });
        return socket;
    }

    [Fact]
    public async Task Read_TurnsEachMessageIntoOneLine()
    {
        WebSocketLineStream stream = new(Socket(("{\"a\":", false), ("1}", true), ("{\"b\":2}\n", true)).Object);
        using StreamReader reader = new(stream);

        Assert.Equal("{\"a\":1}", await reader.ReadLineAsync());
        Assert.Equal("{\"b\":2}", await reader.ReadLineAsync());
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task Write_SendsOneTextMessagePerWrite()
    {
        Mock<WebSocket> socket = Socket();
        List<string> sent = [];
        socket.Setup(item => item.SendAsync(It.IsAny<ReadOnlyMemory<byte>>(), WebSocketMessageType.Text, true, It.IsAny<CancellationToken>()))
            .Callback((ReadOnlyMemory<byte> data, WebSocketMessageType _, bool _, CancellationToken _) => sent.Add(Encoding.UTF8.GetString(data.Span)))
            .Returns(ValueTask.CompletedTask);
        WebSocketLineStream stream = new(socket.Object);

        await stream.WriteAsync(Encoding.UTF8.GetBytes("{}\n"));

        Assert.Equal(["{}\n"], sent);
    }
}
