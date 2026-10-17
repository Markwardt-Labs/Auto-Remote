namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChatViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid workspaceId = Guid.NewGuid();

    private readonly ChatViewModel chat;

    public ChatViewModelTests() => chat = new ChatViewModel(connection.Object, workspaceId);

    [Fact]
    public void AutoScroll_IsOnUntilTurnedOff()
    {
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.True(chat.AutoScroll);

        chat.AutoScroll = false;

        Assert.False(chat.AutoScroll);
        Assert.Contains(nameof(ChatViewModel.AutoScroll), changed);
    }

    [Fact]
    public async Task Start_RaisesLoadedOnceTheConversationIsShown()
    {
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }], Start = 0 });
        int shown = -1;
        chat.Loaded += () => shown = chat.Messages.Count;

        await chat.Start();

        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task Start_RestoresTheSavedDraft()
    {
        Mock<IDraftStore> drafts = new();
        drafts.Setup(x => x.Load(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync("half written");
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        ChatViewModel restoring = new(connection.Object, workspaceId, drafts.Object);

        await restoring.Start();

        Assert.Equal("half written", restoring.Input);
        Assert.True(restoring.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_KeepsWhatWasTypedBeforeItFinished()
    {
        Mock<IDraftStore> drafts = new();
        drafts.Setup(x => x.Load(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync("old draft");
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        ChatViewModel restoring = new(connection.Object, workspaceId, drafts.Object) { Input = "typed already" };

        await restoring.Start();

        Assert.Equal("typed already", restoring.Input);
    }

    [Fact]
    public async Task Input_IsSavedAsADraftOnceTypingPausesAndClearedWhenSent()
    {
        Mock<IDraftStore> drafts = new();
        ChatViewModel typing = new(connection.Object, workspaceId, drafts.Object);

        typing.Input = "h";
        typing.Input = "hello";
        await Until(() => drafts.Invocations.Any(call => call.Method.Name == nameof(IDraftStore.Save)));

        drafts.Verify(x => x.Save(workspaceId, "hello", It.IsAny<CancellationToken>()), Times.Once);
        drafts.Verify(x => x.Save(workspaceId, "h", It.IsAny<CancellationToken>()), Times.Never);

        typing.SendCommand.Execute(null);
        await Until(() => typing.Input.Length == 0);
        await Until(() => drafts.Invocations.Count(call => call.Method.Name == nameof(IDraftStore.Save)) == 2);

        drafts.Verify(x => x.Save(workspaceId, string.Empty, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Resync_ReadsTheMessagesAndWorkingStateThatWerePostedWhileTheConnectionWasDown()
    {
        ChatMessage one = new() { Role = MessageRole.User, Text = "one" };
        ChatMessage two = new() { Role = MessageRole.Assistant, Text = "two" };
        connection.SetupSequence(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatHistory { Messages = [one], Start = 0, Working = true })
            .ReturnsAsync(new ChatHistory { Messages = [one, two], Start = 0, Working = false });
        await chat.Start();
        Assert.True(chat.IsAiWorking);

        await chat.Resync();

        Assert.Equal([one, two], chat.Messages);
        Assert.False(chat.IsAiWorking);
    }

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
