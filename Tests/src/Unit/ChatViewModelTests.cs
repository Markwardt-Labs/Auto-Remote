namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChatViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid workspaceId = Guid.NewGuid();

    private readonly ChatViewModel chat;

    public ChatViewModelTests() => chat = new ChatViewModel(connection.Object, workspaceId);

    [Fact]
    public async Task Send_UploadsAttachmentsAndRefersToThemInThePrompt()
    {
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.png", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/srv/a.png");
        chat.Input = "look";
        chat.Attach("a.png", new byte[] { 1, 2 });

        chat.SendCommand.Execute(null);
        await Until(() => chat.Attachments.Count == 0 && chat.Input.Length == 0);

        connection.Verify(x => x.Prompt(workspaceId, "look\nAttached file: /srv/a.png", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void SendCommand_IsAvailableWithOnlyAnAttachment()
    {
        Assert.False(chat.SendCommand.CanExecute(null));

        chat.Attach("a.txt", new byte[] { 1 });

        Assert.True(chat.SendCommand.CanExecute(null));
    }

    [Fact]
    public void Attach_RejectsEmptyAndOversizedFiles()
    {
        chat.Attach("empty.txt", ReadOnlyMemory<byte>.Empty);
        chat.Attach("big.bin", new byte[chat.MaxAttachment + 1]);

        Assert.Empty(chat.Attachments);
        Assert.True(chat.HasError);
    }

    [Fact]
    public void RemoveAttachmentCommand_DropsTheAttachment()
    {
        chat.Attach("a.txt", new byte[] { 1 });

        chat.RemoveAttachmentCommand.Execute(chat.Attachments[0]);

        Assert.Empty(chat.Attachments);
    }

    [Fact]
    public async Task Send_RestoresTextAndAttachmentsWhenTheUploadFails()
    {
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("too big"));
        chat.Input = "look";
        chat.Attach("a.txt", new byte[] { 1 });

        chat.SendCommand.Execute(null);
        await Until(() => chat.Input == "look");

        Assert.Single(chat.Attachments);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
