namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class MessageChannelTests
{
    private readonly IMessageChannel.Factory factory = new();

    [Fact]
    public async Task Send_WritesCamelCaseJsonLineWithoutNulls()
    {
        using DuplexStream stream = new([]);

        await factory.Create(stream).Send(new RemoteRequest { Id = 3, Command = RemoteCommand.ListFolders }, CancellationToken.None);

        Assert.Equal("{\"id\":3,\"command\":\"ListFolders\",\"deleteFolder\":true}\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
    }

    [Fact]
    public async Task Receive_ReadsMessagesInOrderThenNull()
    {
        using DuplexStream stream = new(Encoding.UTF8.GetBytes("{\"id\":1,\"command\":\"ListWorkspaces\"}\n{\"id\":2,\"command\":\"Prompt\",\"text\":\"hi\"}\n"));
        IMessageChannel channel = factory.Create(stream);

        RemoteRequest? first = await channel.Receive<RemoteRequest>(CancellationToken.None);
        RemoteRequest? second = await channel.Receive<RemoteRequest>(CancellationToken.None);

        Assert.Equal(RemoteCommand.ListWorkspaces, first?.Command);
        Assert.Equal("hi", second?.Text);
        Assert.Null(await channel.Receive<RemoteRequest>(CancellationToken.None));
    }

    [Fact]
    public async Task Receive_ThrowsOnInvalidJson()
    {
        using DuplexStream stream = new(Encoding.UTF8.GetBytes("not json\n"));

        await Assert.ThrowsAsync<InvalidDataException>(() => factory.Create(stream).Receive<RemoteRequest>(CancellationToken.None));
    }

    [Fact]
    public async Task RoundTrip_PreservesWorkspaceAndDerivedName()
    {
        using MemoryStream stream = new();
        IMessageChannel channel = factory.Create(stream);
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/home/me/project" };

        await channel.Send(new RemoteResponse { Id = 1, Workspace = workspace }, CancellationToken.None);
        stream.Position = 0;
        RemoteResponse? response = await factory.Create(stream).Receive<RemoteResponse>(CancellationToken.None);

        Assert.Equal(workspace, response?.Workspace);
        Assert.Equal("project", response?.Workspace?.Name);
    }

    [Theory]
    [InlineData("/home/me/project", "project")]
    [InlineData("/home/me/project/", "project")]
    [InlineData("C:\\Work\\app", "app")]
    [InlineData("/", "/")]
    public void WorkspaceName_IsFolderName(string folder, string name) => Assert.Equal(name, new WorkspaceInfo { Id = Guid.Empty, Folder = folder }.Name);

    [Fact]
    public async Task Events_TravelAsResponsesWithIdZero()
    {
        using DuplexStream stream = new([]);
        RemoteEvent remoteEvent = new() { Kind = RemoteEventKind.MessageAdded, WorkspaceId = Guid.Empty, Index = 4, Message = new ChatMessage { Role = MessageRole.Assistant, Text = "hi" } };

        await factory.Create(stream).Send(new RemoteResponse { Id = 0, Event = remoteEvent }, CancellationToken.None);
        string line = Encoding.UTF8.GetString(stream.Written.ToArray());

        Assert.Equal("{\"id\":0,\"event\":{\"kind\":\"MessageAdded\",\"workspaceId\":\"00000000-0000-0000-0000-000000000000\",\"index\":4,\"message\":{\"role\":\"Assistant\",\"text\":\"hi\"}}}\n", line);
        using DuplexStream incoming = new(Encoding.UTF8.GetBytes(line));
        Assert.Equal(remoteEvent, (await factory.Create(incoming).Receive<RemoteResponse>(CancellationToken.None))?.Event);
    }
}
