namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ServerConnectionTests
{
    private Mock<IMessageChannel> Channel(List<RemoteRequest> sent, out Channel<RemoteResponse> replies)
    {
        Channel<RemoteResponse> queue = System.Threading.Channels.Channel.CreateUnbounded<RemoteResponse>();
        replies = queue;
        Mock<IMessageChannel> channel = new();
        channel.Setup(item => item.Send(It.IsAny<RemoteRequest>(), It.IsAny<CancellationToken>())).Callback((RemoteRequest request, CancellationToken _) =>
        {
            sent.Add(request);
            queue.Writer.TryWrite(new RemoteResponse { Id = request.Id });
        }).Returns(Task.CompletedTask);
        channel.Setup(item => item.Receive<RemoteResponse>(It.IsAny<CancellationToken>())).Returns(async (CancellationToken cancellation) => await queue.Reader.WaitToReadAsync(cancellation) ? await queue.Reader.ReadAsync(cancellation) : null);
        return channel;
    }

    [Fact]
    public async Task Request_ReopensALostConnectionAndReplaysTheFileWatch()
    {
        List<RemoteRequest> sentFirst = [];
        List<RemoteRequest> sentSecond = [];
        Mock<IMessageChannel> first = Channel(sentFirst, out Channel<RemoteResponse> firstReplies);
        Mock<IMessageChannel> second = Channel(sentSecond, out _);
        int opened = 0;
        await using ServerConnection connection = new(new ClientWebSocket(), first.Object, _ =>
        {
            opened++;
            return Task.FromResult((new ClientWebSocket(), second.Object));
        }, Mock.Of<ILog>(), "box:1");
        Guid workspace = Guid.NewGuid();

        await connection.WatchFiles(workspace, ["src"], CancellationToken.None);
        firstReplies.Writer.Complete();
        await Task.Delay(100);
        await connection.Prompt(workspace, "hi", CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Equal([RemoteCommand.WatchFiles, RemoteCommand.Prompt], sentSecond.Select(request => request.Command));
        Assert.Equal(["src"], sentSecond[0].Paths);
    }

    [Fact]
    public async Task Request_FailsWhenTheServerCannotBeReached()
    {
        List<RemoteRequest> sent = [];
        Mock<IMessageChannel> first = Channel(sent, out Channel<RemoteResponse> replies);
        await using ServerConnection connection = new(new ClientWebSocket(), first.Object, _ => throw new IOException("down"), Mock.Of<ILog>(), "box:1");

        replies.Writer.Complete();
        await Task.Delay(100);

        await Assert.ThrowsAsync<IOException>(() => connection.ListWorkspaces(CancellationToken.None));
    }

    [Fact]
    public async Task ALostConnectionIsReopenedByItselfAndSaysSoSoScreensCanReadWhatTheyMissed()
    {
        Mock<IMessageChannel> first = Channel([], out Channel<RemoteResponse> firstReplies);
        Mock<IMessageChannel> second = Channel([], out _);
        int opened = 0;
        int lost = 0;
        TaskCompletionSource reconnected = new();
        await using ServerConnection connection = new(new ClientWebSocket(), first.Object, _ =>
        {
            opened++;
            return Task.FromResult((new ClientWebSocket(), second.Object));
        }, Mock.Of<ILog>(), "box:1");
        connection.Lost += () => lost++;
        connection.Reconnected += () => reconnected.TrySetResult();

        firstReplies.Writer.Complete();
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, lost);
        Assert.Equal(1, opened);
        await connection.Prompt(Guid.NewGuid(), "x", CancellationToken.None);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task Reconnect_ReplacesAConnectionThatLooksAlive()
    {
        List<RemoteRequest> sentSecond = [];
        Mock<IMessageChannel> first = Channel([], out Channel<RemoteResponse> firstReplies);
        Mock<IMessageChannel> second = Channel(sentSecond, out _);
        int opened = 0;
        int reconnected = 0;
        await using ServerConnection connection = new(new ClientWebSocket(), first.Object, _ =>
        {
            opened++;
            return Task.FromResult((new ClientWebSocket(), second.Object));
        }, Mock.Of<ILog>(), "box:1");
        connection.Reconnected += () => reconnected++;

        Task reconnecting = connection.Reconnect(CancellationToken.None);
        firstReplies.Writer.Complete();
        await reconnecting;
        await connection.Prompt(Guid.NewGuid(), "x", CancellationToken.None);

        Assert.Equal(1, opened);
        Assert.Equal(1, reconnected);
        Assert.Equal([RemoteCommand.Prompt], sentSecond.Select(request => request.Command));
    }
}
