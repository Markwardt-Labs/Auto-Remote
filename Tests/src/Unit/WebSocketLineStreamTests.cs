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

    private (string text, bool end)[] Frames(string text) => [.. Enumerable.Range(0, (text.Length + 3999) / 4000).Select(index => (text.Substring(index * 4000, Math.Min(4000, text.Length - index * 4000)), (index + 1) * 4000 >= text.Length))];

    [Fact]
    public async Task Read_AcceptsALargeMessageAfterTheFirstOneAndRefusesALongFirstOne()
    {
        string large = new('a', 30 * 1024 * 1024);
        using StreamReader reader = new(new WebSocketLineStream(Socket([("{}", true), .. Frames(large)]).Object, 4096));

        Assert.Equal("{}", await reader.ReadLineAsync());
        Assert.Equal(large.Length, (await reader.ReadLineAsync())!.Length);

        using StreamReader refused = new(new WebSocketLineStream(Socket(Frames(new string('b', 5000))).Object, 4096));

        await Assert.ThrowsAsync<InvalidDataException>(() => refused.ReadLineAsync());
    }

    [Fact]
    public async Task Read_RefusesAMessageLongerThanTheLimit()
    {
        using StreamReader reader = new(new WebSocketLineStream(Socket([("{}", true), .. Frames(new string('c', 41 * 1024 * 1024))]).Object, 4096));

        Assert.Equal("{}", await reader.ReadLineAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync());
    }
}
