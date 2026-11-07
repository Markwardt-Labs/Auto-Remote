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
    public void HasContent_IsTrueForTextOrAFileAndOtherwiseTheReviewCommandIsOffered()
    {
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.Equal((false, true, false, true), (chat.HasContent, chat.ReviewCommand.CanExecute(null), chat.SendCommand.CanExecute(null), chat.ExpandCommand.CanExecute(null)));

        chat.Input = "  ";
        Assert.False(chat.HasContent);

        chat.Input = "fix it";
        Assert.Equal((true, false, true, false), (chat.HasContent, chat.ReviewCommand.CanExecute(null), chat.SendCommand.CanExecute(null), chat.ExpandCommand.CanExecute(null)));

        chat.Input = string.Empty;
        chat.Attach("a.txt", new byte[] { 1 });
        Assert.Equal((true, false, true), (chat.HasContent, chat.ReviewCommand.CanExecute(null), chat.SendCommand.CanExecute(null)));
        Assert.Contains(nameof(ChatViewModel.HasContent), changed);
    }

    [Theory]
    [InlineData(true, "pause current work")]
    [InlineData(false, "resume unfinished work")]
    public async Task PauseAndResume_SendTheirFixedPromptAsTheUsersMessageAndAreOfferedOnlyWhileNothingIsTyped(bool pause, string expected)
    {
        string? sent = null;
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, text, _) => sent = text).Returns(Task.CompletedTask);
        AsyncCommand command = pause ? chat.PauseCommand : chat.ResumeCommand;

        chat.Input = "typed";
        bool whileTyped = command.CanExecute(null);
        chat.Input = string.Empty;
        bool whenEmpty = command.CanExecute(null);
        command.Execute(null);
        for (int attempt = 0; attempt < 100 && sent is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal((false, true, expected), (whileTyped, whenEmpty, sent));
        Assert.Equal(("pause current work", "resume unfinished work"), (chat.PausePrompt, chat.ResumePrompt));
    }

    [Theory]
    [InlineData(true, null, "review workspace and fix any problems or inconsistencies")]
    [InlineData(true, "  review this workspace my way ", "review this workspace my way")]
    [InlineData(false, null, "expand upon content in workspace in areas that make sense")]
    [InlineData(false, "grow the docs", "grow the docs")]
    public async Task ReviewAndExpand_SendThePromptOfTheWorkspaceOrTheGlobalOneAsTheUsersMessageWithoutTouchingTheMessageBox(bool review, string? own, string expected)
    {
        connection.Setup(x => x.GetWorkspaceSettings(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { ReviewPrompt = review ? own : null, ExpandPrompt = review ? null : own, GlobalReviewPrompt = "review workspace and fix any problems or inconsistencies", GlobalExpandPrompt = "expand upon content in workspace in areas that make sense" });
        string? sent = null;
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, text, _) => sent = text).Returns(Task.CompletedTask);
        int started = 0;
        chat.PromptStarted += () => started++;

        (review ? chat.ReviewCommand : chat.ExpandCommand).Execute(null);
        for (int attempt = 0; attempt < 100 && sent is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(expected, sent);
        Assert.Equal((1, string.Empty), (started, chat.Input));
        connection.Verify(x => x.UploadAttachment(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Review_WhenTheServerRefusesReportsTheFailureAndDoesNotPutThePromptInTheMessageBox()
    {
        connection.Setup(x => x.GetWorkspaceSettings(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review" });
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("A script is running."));
        bool failed = false;
        chat.PromptFailed += () => failed = true;

        chat.ReviewCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && !failed; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(failed);
        Assert.Equal("A script is running.", chat.Error);
        Assert.Equal(string.Empty, chat.Input);
    }

    [Fact]
    public async Task Expand_WhenThePromptCannotBeReadOrIsEmptySendsNothingAndSaysSo()
    {
        connection.Setup(x => x.GetWorkspaceSettings(workspaceId, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        chat.ExpandCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && chat.Error is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("down", chat.Error);

        connection.Setup(x => x.GetWorkspaceSettings(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        chat.ExpandCommand.Execute(null);
        for (int attempt = 0; attempt < 100 && chat.Error == "down"; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("The prompt of this button is empty.", chat.Error);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Review_IsUnavailableWhileAScriptOrGitBlocksTheAi()
    {
        chat.BlockReason = "A script is running, wait for it to finish";

        Assert.False(chat.ReviewCommand.CanExecute(null));

        chat.BlockReason = null;
        Assert.True(chat.ReviewCommand.CanExecute(null));
    }

    [Fact]
    public async Task Start_RaisesLoadedOnceTheConversationIsShown()
    {
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }] });
        int shown = -1;
        chat.Loaded += () => shown = chat.Messages.Count;

        await chat.Start();

        Assert.Equal(1, shown);
    }

    [Fact]
    public async Task Status_ShowsTheLatestStatusOfTheAiWhileItWorksAndIsHiddenAgainWhenTheWorkEnds()
    {
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        await chat.Start();
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.False(chat.HasStatus);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspaceId, Working = true });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StatusChanged, WorkspaceId = workspaceId, Text = "reading the files" });
        Assert.Equal(("reading the files", true), (chat.Status, chat.HasStatus));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StatusChanged, WorkspaceId = workspaceId, Text = "writing the tests" });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StatusChanged, WorkspaceId = Guid.NewGuid(), Text = "another workspace" });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StatusChanged, WorkspaceId = workspaceId, Text = "  " });
        Assert.Equal("writing the tests", chat.Status);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspaceId, Working = false });
        Assert.Equal((null, false), (chat.Status, chat.HasStatus));
        Assert.Contains(nameof(ChatViewModel.HasStatus), changed);
    }

    [Theory]
    [InlineData(true, "halfway", "halfway")]
    [InlineData(false, "stale", null)]
    public async Task Start_ShowsTheStatusTheServerHasWhileTheAiWorksAndNoneOtherwise(bool working, string status, string? expected)
    {
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Working = working, Status = status });

        await chat.Start();

        Assert.Equal(expected, chat.Status);
    }

    [Fact]
    public async Task Start_RestoresTheSavedDraft()
    {
        Mock<IDraftStore> drafts = new();
        drafts.Setup(x => x.Load(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync("half written");
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
            .ReturnsAsync(new ChatHistory { Messages = [one], Working = true })
            .ReturnsAsync(new ChatHistory { Messages = [one, two], Working = false });
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
