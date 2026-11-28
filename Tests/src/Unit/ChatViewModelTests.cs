namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ChatViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid workspaceId = Guid.NewGuid();

    private readonly ChatViewModel chat;

    public ChatViewModelTests() => chat = new ChatViewModel(connection.Object, workspaceId) { ChunkMessages = 20 };

    [Fact]
    public async Task APromptThatFailsAfterMoreWasTypedComesBackBeforeItWithItsAttachments()
    {
        TaskCompletionSource sending = new();
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/data/a.txt");
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(sending.Task);
        chat.Input = "long prompt";
        chat.Attach("a.txt", new byte[] { 1 });
        chat.SendCommand.Execute(null);
        chat.Input = "n";
        chat.Attach("b.txt", new byte[] { 2 });

        sending.SetException(new RemoteException("The connection was lost."));
        await Until(() => chat.Error is not null && chat.Input.Length > 1);

        Assert.Equal("long prompt\nn", chat.Input);
        Assert.Equal(["a.txt", "b.txt"], chat.Attachments.Select(attachment => attachment.Name));
    }

    [Theory]
    [InlineData("keep going", false)]
    [InlineData("keep going", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    public async Task APromptConfirmedOverTheLimitTakesItsAttachmentsOutOfTheBoxAndPutsEverythingBackWhenItFails(string text, bool fails)
    {
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/data/a.txt");
        Refuse(text.Length == 0 ? "Attached file: /data/a.txt" : $"{text}\nAttached file: /data/a.txt");

        if (fails)
        {
            connection.Setup(x => x.PromptOverLimit(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("No."));
        }

        chat.Input = text;
        chat.Attach("a.txt", new byte[] { 1 });
        chat.SendCommand.Execute(null);
        await Until(() => chat.IsAskingOverLimit);

        chat.ConfirmOverLimitCommand.Execute(null);

        Assert.Equal(fails ? text : string.Empty, chat.Input);
        Assert.Equal(fails ? 1 : 0, chat.Attachments.Count);
    }

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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReviewAndExpand_AreAvailableWhileTheAiWorksAndSendTheirPromptToTheWorkInProgress(bool review)
    {
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetWorkspaceSettings(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { GlobalReviewPrompt = "review it", GlobalExpandPrompt = "expand it" });
        string? sent = null;
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, text, _) => sent = text).Returns(Task.CompletedTask);
        await chat.Start();
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspaceId, Working = true });
        AsyncCommand command = review ? chat.ReviewCommand : chat.ExpandCommand;

        Assert.True(chat.IsAiWorking);
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        for (int attempt = 0; attempt < 100 && sent is null; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(review ? "review it" : "expand it", sent);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [new ChatMessage { Role = MessageRole.User, Text = "hi" }] });
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.SetupSequence(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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

    private SavedInput Saved(string text, int minutes = 0) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UnixEpoch.AddMinutes(minutes) };

    [Fact]
    public void HasText_OffersSavingWhileTextIsTypedAndOtherwiseLoading()
    {
        Assert.Equal((false, false), (chat.HasText, chat.SaveInputCommand.CanExecute(null)));

        chat.Input = "  ";
        Assert.Equal((false, false), (chat.HasText, chat.SaveInputCommand.CanExecute(null)));

        chat.Input = "later";
        Assert.Equal((true, true), (chat.HasText, chat.SaveInputCommand.CanExecute(null)));
    }

    [Fact]
    public async Task SaveInput_SavesTheTextOnTheServerShowsTheListAndClearsTheMessageBox()
    {
        SavedInput entry = Saved("later");
        connection.Setup(x => x.SaveInput(workspaceId, "later", true, It.IsAny<CancellationToken>())).ReturnsAsync([entry]);
        chat.Input = "later";

        chat.SaveInputCommand.Execute(null);
        await Until(() => chat.Input.Length == 0);

        Assert.Equal([entry], chat.SavedInputs);
        Assert.False(chat.HasNoDrafts);
        Assert.True(chat.HasDrafts);
    }

    [Fact]
    public async Task SaveInput_KeepsTheTextAndShowsTheErrorWhenTheServerRefuses()
    {
        connection.Setup(x => x.SaveInput(workspaceId, "later", true, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The input is too long."));
        chat.Input = "later";

        chat.SaveInputCommand.Execute(null);
        await Until(() => chat.Error is not null);

        Assert.Equal(("later", "The input is too long."), (chat.Input, chat.Error));
        Assert.Empty(chat.SavedInputs);
    }

    [Fact]
    public async Task LoadSavedInput_TakesTheEntryOffTheServerAndPutsItIntoTheMessageBoxAndClosesTheList()
    {
        SavedInput entry = Saved("later");
        connection.Setup(x => x.TakeSavedInput(workspaceId, entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        chat.ToggleSavedInputsCommand.Execute(null);
        Assert.True(chat.IsSavedInputsOpen);

        chat.LoadSavedInputCommand.Execute(entry);
        await Until(() => chat.Input == "later");

        Assert.False(chat.IsSavedInputsOpen);
    }

    [Fact]
    public async Task LoadSavedInput_ReadsTheListAgainAndKeepsTheErrorWhenAnotherClientTookTheEntryFirst()
    {
        SavedInput gone = Saved("gone");
        SavedInput left = Saved("left");
        connection.Setup(x => x.TakeSavedInput(workspaceId, gone.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("That saved input is gone."));
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([left]);

        chat.LoadSavedInputCommand.Execute(gone);
        await Until(() => chat.SavedInputs.Count == 1);

        Assert.Equal(("", "That saved input is gone."), (chat.Input, chat.Error));
        Assert.Equal([left], chat.SavedInputs);
    }

    [Fact]
    public async Task LoadSavedInput_KeepsWhatWasTypedMeanwhileBehindTheLoadedText()
    {
        SavedInput entry = Saved("saved");
        connection.Setup(x => x.TakeSavedInput(workspaceId, entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);
        chat.Input = "typed";

        chat.LoadSavedInputCommand.Execute(entry);
        await Until(() => chat.Input == "saved\ntyped");
    }

    [Fact]
    public async Task SavedInputs_AreReadWithTheConversationAndFollowTheEventsOfEveryClient()
    {
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        SavedInput first = Saved("first", 1);
        SavedInput second = Saved("second", 2);
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([first]);
        await chat.Start();
        Assert.Equal([first], chat.SavedInputs);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspaceId, SavedInputs = [second, first] });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = Guid.NewGuid(), SavedInputs = [] });

        Assert.Equal([second, first], chat.SavedInputs);
        Assert.True(chat.HasDrafts);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspaceId, SavedInputs = [] });

        Assert.True(chat.HasNoDrafts);
        Assert.False(chat.HasDrafts);
    }

    [Fact]
    public void Typing_ClosesTheListOfDrafts()
    {
        chat.ToggleSavedInputsCommand.Execute(null);

        chat.Input = "typing";

        Assert.False(chat.IsSavedInputsOpen);
    }

    private static ChatMessage Thought(string text) => new() { Role = MessageRole.Assistant, Text = text, IsWork = true, IsThought = true, Time = DateTimeOffset.UtcNow };

    private RemoteEvent Piece(string text, bool thought) => new() { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = text, Thought = thought ? true : null };

    [Fact]
    public async Task TheReasoningBeingStreamedIsAWorkMessageWithATimeThatTheFinishedReasoningTakesTheSeatOfBeforeTheAnswerStreams()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(true, Said("before"));
        await chat.Start();

        connection.Raise(x => x.EventReceived += null, Piece("let me ", true));
        connection.Raise(x => x.EventReceived += null, Piece("think", true));

        ChatMessage streamed = chat.Messages[^1];
        Assert.Equal(("let me think", true, true), (streamed.Text, streamed.IsWork, streamed.IsThought));
        Assert.NotNull(streamed.Time);

        connection.Raise(x => x.EventReceived += null, Added(Thought("let me think")));
        connection.Raise(x => x.EventReceived += null, Piece("The ", false));
        connection.Raise(x => x.EventReceived += null, Piece("answer", false));
        connection.Raise(x => x.EventReceived += null, Added(Said("The answer")));

        Assert.Equal(["before", "let me think", "The answer"], chat.Messages.Select(message => message.Text));
        Assert.Equal([false, true, false], chat.Messages.Select(message => message.IsThought));
        Assert.Equal([false, true, false], chat.Messages.Select(message => message.IsWork));
    }

    [Fact]
    public async Task WorkAddedWhileTheReasoningStreamsComesAfterItAndThenTheReasoningIsFinishedInPlace()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(true, Said("before"));
        await chat.Start();
        connection.Raise(x => x.EventReceived += null, Piece("thinking", true));

        connection.Raise(x => x.EventReceived += null, Added(Thought("thinking")));
        connection.Raise(x => x.EventReceived += null, Added(Did("Read a.txt")));

        Assert.Equal(["before", "thinking", "Read a.txt"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ReasoningThatTheWorkspaceDoesNotShowIsNotStreamedAndTheStoredOneStaysHidden()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false, Said("before"));
        await chat.Start();

        connection.Raise(x => x.EventReceived += null, Piece("secret", true));
        connection.Raise(x => x.EventReceived += null, Added(Thought("secret")));

        Assert.Equal(["before"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task AChunkIsMeasuredInTextSoFewLongMessagesAndManyShortOnesFillAWindowAlike()
    {
        ChatViewModel measured = new(connection.Object, workspaceId);
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, 100, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [.. Enumerable.Range(0, 5).Select(number => Said($"{number}{new string('x', 6000)}"))], Older = "10" });
        await measured.Start();
        measured.AutoScroll = false;

        for (int number = 5; number < 8; number++)
        {
            connection.Raise(x => x.EventReceived += null, Added(Said($"{number}{new string('x', 6000)}")));
        }

        Assert.Equal(8, measured.Messages.Count);
        Assert.True(measured.TrimToNewest());
        Assert.Equal(Enumerable.Range(5, 3).Select(number => number.ToString()), measured.Messages.Select(message => message.Text[..1]));
        measured.AutoScroll = true;

        for (int number = 0; number < 300; number++)
        {
            connection.Raise(x => x.EventReceived += null, Added(Said($"→ {number}")));
        }

        Assert.Equal(100, measured.Messages.Count);
        Assert.Equal("→ 299", measured.Messages[^1].Text);
    }

    [Fact]
    public async Task TurningTheWorkOnAfterScrollingUpReloadsTheSameStretchOfTheConversationNotJustTheNewestChunk()
    {
        ChatViewModel measured = new(connection.Object, workspaceId) { ChunkMessages = 3 };
        List<ChatMessage> before = [Said("m1"), Said("m2"), Said("m3"), Said("m4"), Said("m5")];
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [before[2], before[3], before[4]], Older = "2" });
        connection.Setup(x => x.GetHistory(workspaceId, "2", 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [before[0], before[1]], Older = null });
        await measured.Start();
        Assert.True(await measured.LoadOlder());
        Assert.Equal(["m1", "m2", "m3", "m4", "m5"], measured.Messages.Select(message => message.Text));
        measured.AutoScroll = false;
        List<ChatMessage> withWork = [Said("m1"), Did("Read a"), Did("→ 3 lines"), Said("m2"), Did("Run x"), Did("→ done"), Said("m3"), Did("Read b"), Did("→ 9 lines"), Said("m4"), Did("Read c"), Said("m5")];
        connection.Setup(x => x.GetHistory(workspaceId, null, 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = withWork.TakeLast(3).ToList(), Older = "9", ShowWork = true });
        connection.Setup(x => x.GetHistory(workspaceId, "9", 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = withWork.Skip(6).Take(3).ToList(), Older = "6", ShowWork = true });
        connection.Setup(x => x.GetHistory(workspaceId, "6", 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = withWork.Skip(3).Take(3).ToList(), Older = "3", ShowWork = true });
        connection.Setup(x => x.GetHistory(workspaceId, "3", 3, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = withWork.Take(3).ToList(), Older = null, ShowWork = true });

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId, ShowWork = true });
        for (int attempt = 0; attempt < 200 && measured.Messages.Count < withWork.Count; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(withWork.Select(message => message.Text), measured.Messages.Select(message => message.Text));
        Assert.False(measured.HasMore);
    }

    private ChatMessage Asked(string text) => new() { Role = MessageRole.User, Text = text, Time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };

    // A server that has not answered a prompt yet, as one that is still working out whether and how to start on it.
    private TaskCompletionSource HoldPrompts()
    {
        TaskCompletionSource answer = new();
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(answer.Task);
        // The runner waits for the command that sends, so a test that failed before it answered must not wait for ever.
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => answer.TrySetResult(), TaskScheduler.Default);
        return answer;
    }

    // A chat follows the server once it was started.
    private async Task Started(params ChatMessage[] messages)
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false, messages);
        await chat.Start();
    }

    private void Submit(string text)
    {
        chat.Input = text;
        chat.SendCommand.Execute(null);
    }

    private List<(string Text, bool Pending)> Shown() => [.. chat.Messages.Select(message => (message.Text, message.IsPending))];

    [Fact]
    public void ASentPromptShowsAtOnceBeforeTheServerAnswers()
    {
        TaskCompletionSource answer = HoldPrompts();
        int arrived = 0;
        chat.OutputArrived += () => arrived++;

        Submit("  fix it  ");

        ChatMessage shown = Assert.Single(chat.Messages);
        Assert.Equal((MessageRole.User, "fix it", true, null), (shown.Role, shown.Text, shown.IsPending, shown.Time));
        Assert.Equal((1, string.Empty), (arrived, chat.Input));
        answer.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheServersCopyTakesThePlaceOfTheOneShownWhicheverComesFirstItOrTheAnswer(bool answeredFirst)
    {
        await Started();
        TaskCompletionSource answer = HoldPrompts();
        Submit("fix it");
        int arrived = 0;
        chat.OutputArrived += () => arrived++;

        if (answeredFirst)
        {
            answer.SetResult();
            Assert.Equal([("fix it", true)], Shown());
        }

        connection.Raise(x => x.EventReceived += null, Added(Asked("fix it")));
        answer.TrySetResult();

        Assert.Equal([("fix it", false)], Shown());
        Assert.NotNull(chat.Messages[0].Time);
        Assert.Equal(0, arrived);
    }

    [Fact]
    public async Task WhatArrivesWhileAPromptIsOnItsWayComesBeforeIt()
    {
        await Started();
        TaskCompletionSource answer = HoldPrompts();
        chat.ShowWork = true;
        connection.Raise(x => x.EventReceived += null, Added(Said("working")));
        Submit("also this");

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = "still wri" });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = "ting" });
        Assert.Equal([("working", false), ("still writing", false), ("also this", true)], Shown());

        connection.Raise(x => x.EventReceived += null, Added(Said("still writing")));
        connection.Raise(x => x.EventReceived += null, Added(Said("one more")));
        connection.Raise(x => x.EventReceived += null, Added(Asked("also this")));
        connection.Raise(x => x.EventReceived += null, Added(Said("after")));

        Assert.Equal([("working", false), ("still writing", false), ("one more", false), ("also this", false), ("after", false)], Shown());
        answer.SetResult();
    }

    [Fact]
    public async Task EveryPromptOnItsWayIsReplacedOnceAlsoWhenTheyReadTheSameOrArriveOutOfOrder()
    {
        await Started();
        TaskCompletionSource answer = HoldPrompts();
        Submit("go");
        Submit("stop");
        Submit("go");

        connection.Raise(x => x.EventReceived += null, Added(Asked("stop")));
        Assert.Equal([("stop", false), ("go", true), ("go", true)], Shown());

        connection.Raise(x => x.EventReceived += null, Added(Asked("go")));
        Assert.Equal([("stop", false), ("go", false), ("go", true)], Shown());

        connection.Raise(x => x.EventReceived += null, Added(Asked("go")));
        Assert.Equal([("stop", false), ("go", false), ("go", false)], Shown());

        // One sent from another client is simply added.
        connection.Raise(x => x.EventReceived += null, Added(Asked("go")));
        Assert.Equal(4, chat.Messages.Count);
        answer.SetResult();
    }

    [Fact]
    public async Task APromptTheServerDoesNotTakeLeavesTheChatAndComesBackToTheBox()
    {
        TaskCompletionSource answer = HoldPrompts();
        Submit("fix it");

        answer.SetException(new RemoteException("The workspace is busy."));
        await Until(() => chat.Error is not null);

        Assert.Empty(chat.Messages);
        Assert.Equal(("fix it", "The workspace is busy."), (chat.Input, chat.Error));
    }

    [Fact]
    public async Task APromptOverTheLimitLeavesTheChatUntilItIsConfirmed()
    {
        TaskCompletionSource confirmed = HoldPrompts();
        Refuse("keep going");
        connection.Setup(x => x.PromptOverLimit(workspaceId, "keep going", It.IsAny<CancellationToken>())).Returns(confirmed.Task);
        Submit("keep going");
        await Until(() => chat.IsAskingOverLimit);
        Assert.Empty(chat.Messages);

        chat.ConfirmOverLimitCommand.Execute(null);

        Assert.Equal([("keep going", true)], Shown());
        confirmed.SetResult();
    }

    [Fact]
    public async Task APromptThatHasToWaitIsLeftToTheQueueBanner()
    {
        await Started();
        TaskCompletionSource answer = HoldPrompts();
        Submit("later");
        Submit("not queued");

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspaceId, Queue = new QueueStatus { Position = 1, Text = "earlier\n\nlater" } });

        Assert.Equal([("not queued", true)], Shown());
        Assert.True(chat.IsQueued);
        answer.SetResult();
    }

    [Fact]
    public async Task AttachedFilesShowByNameWhileTheyUploadAndThePromptStillMeetsTheServersCopy()
    {
        await Started();
        TaskCompletionSource<string> upload = new();
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Returns(upload.Task);
        TaskCompletionSource answer = HoldPrompts();
        chat.Attach("a.txt", new byte[] { 1 });
        Submit("read this");
        Assert.Equal([("read this\nAttached file: a.txt", true)], Shown());

        upload.SetResult("/data/12345678-a.txt");
        await Until(() => chat.Messages[0].Text.Contains("/data/", StringComparison.Ordinal));
        connection.Raise(x => x.EventReceived += null, Added(Asked("read this\nAttached file: /data/12345678-a.txt")));

        Assert.Equal([("read this\nAttached file: /data/12345678-a.txt", false)], Shown());
        answer.SetResult();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingTheChatAgainKeepsAPromptOnItsWayUnlessWhatWasReadHoldsIt(bool arrived)
    {
        // The same words were said before, which are not the prompt on its way.
        await Started(Asked("go"), Said("done"));
        TaskCompletionSource answer = HoldPrompts();
        Submit("go");
        History(false, arrived ? [Asked("go"), Said("done"), Asked("go") with { Time = DateTimeOffset.UtcNow }] : [Asked("go"), Said("done")]);

        await chat.Resync();

        Assert.Equal([("go", false), ("done", false), ("go", !arrived)], Shown());
        answer.SetResult();
    }

    [Fact]
    public async Task TrimmingNeverDropsAPromptOnItsWayNorCountsItAsStored()
    {
        await WithHistory(1000, 20);
        TaskCompletionSource answer = HoldPrompts();

        // The second prompt alone is more text than a chunk holds, which would drop the first with everything before it.
        Submit("first");
        Submit(new string('x', 25_000));

        Assert.Equal([true, true], chat.Messages.Select(message => message.IsPending));

        // Only the stored messages were dropped and counted, so the older ones load from right where they ended.
        Assert.True(await chat.LoadOlder());
        Assert.Equal(("m980", "m999", 22), (chat.Messages[0].Text, chat.Messages[19].Text, chat.Messages.Count));
        connection.Verify(x => x.GetHistory(workspaceId, "1000", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);
        answer.SetResult();
    }

    private static ChatMessage Said(string text) => new() { Role = MessageRole.Assistant, Text = text };

    private static ChatMessage Did(string text) => new() { Role = MessageRole.Assistant, Text = text, IsWork = true };

    private RemoteEvent Added(ChatMessage message) => new() { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspaceId, Message = message };

    private void History(bool show, params ChatMessage[] messages) => connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = messages, ShowWork = show });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_TakesWhetherTheWorkShowsFromTheServerWithoutSendingItBack(bool show)
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(show, Said("hello"));
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await chat.Start();

        Assert.Equal(show, chat.ShowWork);
        Assert.Equal(show, changed.Contains(nameof(ChatViewModel.ShowWork)));
        connection.Verify(x => x.SetShowWork(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShowWork_IsOffUntilTurnedOnAndTellsTheServerEachTimeItChanges()
    {
        Assert.False(chat.ShowWork);

        chat.ShowWork = true;
        chat.ShowWork = true;
        chat.ShowWork = false;
        await Task.Delay(100);

        Assert.False(chat.ShowWork);
        connection.Verify(x => x.SetShowWork(workspaceId, true, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SetShowWork(workspaceId, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowWork_GoesBackAndSaysWhyWhenTheServerRefuses()
    {
        connection.Setup(x => x.SetShowWork(workspaceId, true, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The workspace is gone."));

        chat.ShowWork = true;
        for (int attempt = 0; attempt < 100 && chat.ShowWork; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.False(chat.ShowWork);
        Assert.Equal("The workspace is gone.", chat.Error);
    }

    [Fact]
    public async Task TheWorkOfTheAiOnlyAppearsInTheConversationWhileTheWorkShows()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false);
        await chat.Start();

        connection.Raise(x => x.EventReceived += null, Added(Did("shell: ls")));
        connection.Raise(x => x.EventReceived += null, Added(Said("done")));

        Assert.Equal(["done"], chat.Messages.Select(message => message.Text));

        History(true);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId });
        for (int attempt = 0; attempt < 100 && !chat.ShowWork; attempt++)
        {
            await Task.Delay(20);
        }

        connection.Raise(x => x.EventReceived += null, Added(Did("shell: cat a.txt")));
        connection.Raise(x => x.EventReceived += null, Added(Said("more")));

        Assert.True(chat.ShowWork);
        Assert.Equal(["shell: cat a.txt", "more"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ShowWorkChanged_ReadsTheConversationAgainWithOrWithoutTheWorkAndFollowsTheFlag()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false, Said("one"), Said("two"));
        await chat.Start();
        History(true, Said("one"), Did("shell: ls"), Said("two"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId });
        for (int attempt = 0; attempt < 100 && chat.Messages.Count != 3; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(chat.ShowWork);
        Assert.Equal([false, true, false], chat.Messages.Select(message => message.IsWork));

        History(false, Said("one"), Said("two"));
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId });
        for (int attempt = 0; attempt < 100 && chat.Messages.Count != 2; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.False(chat.ShowWork);
        Assert.Equal(["one", "two"], chat.Messages.Select(message => message.Text));
        connection.Verify(x => x.SetShowWork(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ShowWorkChanged_OfAnotherWorkspaceChangesNothing()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false, Said("one"));
        await chat.Start();
        History(true, Said("one"), Did("shell: ls"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = Guid.NewGuid() });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = Guid.NewGuid(), Message = Did("not ours") });
        await Task.Delay(150);

        Assert.False(chat.ShowWork);
        Assert.Equal(["one"], chat.Messages.Select(message => message.Text));
    }

    private RemoteEvent Piece(string text) => new() { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = text };

    private async Task Showing(params ChatMessage[] history)
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(true, history);
        await chat.Start();
    }

    [Fact]
    public async Task TheMessageTheAiIsWritingGrowsInPlaceAndIsReplacedByItsFinishedForm()
    {
        await Showing(new ChatMessage { Role = MessageRole.User, Text = "hi" });

        connection.Raise(x => x.EventReceived += null, Piece("Hel"));
        Assert.Equal(["hi", "Hel"], chat.Messages.Select(message => message.Text));

        connection.Raise(x => x.EventReceived += null, Piece("lo "));
        connection.Raise(x => x.EventReceived += null, Piece("wor"));
        Assert.Equal(["hi", "Hello wor"], chat.Messages.Select(message => message.Text));
        Assert.Equal(MessageRole.Assistant, chat.Messages[1].Role);

        connection.Raise(x => x.EventReceived += null, Added(Said("Hello world")));

        Assert.Equal(["hi", "Hello world"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TheFinishedMessageTakesTheSpotOfTheOneBeingWrittenEvenWhenWorkCameInBetween()
    {
        await Showing();

        connection.Raise(x => x.EventReceived += null, Piece("Let me "));
        connection.Raise(x => x.EventReceived += null, Added(Did("shell: ls")));
        connection.Raise(x => x.EventReceived += null, Added(Said("Let me look")));
        connection.Raise(x => x.EventReceived += null, Piece("Found it"));
        connection.Raise(x => x.EventReceived += null, Added(Said("Found it all")));

        Assert.Equal(["Let me look", "shell: ls", "Found it all"], chat.Messages.Select(message => message.Text));
        Assert.Equal([false, true, false], chat.Messages.Select(message => message.IsWork));
    }

    [Fact]
    public async Task AMessageThatIsNeverFinishedIsDroppedWhenTheAiStopsOrFails()
    {
        await Showing(Said("earlier"));
        connection.Raise(x => x.EventReceived += null, Piece("half a thou"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspaceId, Working = false });

        Assert.Equal(["earlier"], chat.Messages.Select(message => message.Text));

        connection.Raise(x => x.EventReceived += null, Piece("another half"));
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspaceId, Error = "broke" });

        Assert.Equal(["earlier"], chat.Messages.Select(message => message.Text));
        Assert.Equal("broke", chat.Error);

        connection.Raise(x => x.EventReceived += null, Piece("a fresh one"));
        connection.Raise(x => x.EventReceived += null, Added(Said("a fresh one, done")));

        Assert.Equal(["earlier", "a fresh one, done"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task PiecesAreIgnoredWhileTheWorkIsNotShownAndForOtherWorkspaces()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(false);
        await chat.Start();

        connection.Raise(x => x.EventReceived += null, Piece("not shown"));
        connection.Raise(x => x.EventReceived += null, Added(Said("whole")));

        Assert.Equal(["whole"], chat.Messages.Select(message => message.Text));

        History(true, Said("whole"));
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId, ShowWork = true });
        for (int attempt = 0; attempt < 100 && !chat.ShowWork; attempt++)
        {
            await Task.Delay(20);
        }

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = Guid.NewGuid(), Text = "of another workspace" });

        Assert.Equal(["whole"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task ReadingTheConversationAgainDropsTheMessageThatWasBeingWritten()
    {
        await Showing(Said("one"));
        connection.Raise(x => x.EventReceived += null, Piece("partial"));
        History(false, Said("one"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId, ShowWork = false });
        for (int attempt = 0; attempt < 100 && chat.ShowWork; attempt++)
        {
            await Task.Delay(20);
        }

        connection.Raise(x => x.EventReceived += null, Piece("ignored now"));

        Assert.Equal(["one"], chat.Messages.Select(message => message.Text));
    }

    private RemoteEvent Failed(string error, DateTimeOffset? time, Guid? workspace = null) => new() { Kind = RemoteEventKind.Failed, WorkspaceId = workspace ?? workspaceId, Error = error, Time = time };

    private RemoteEvent Typed(string text, Guid? workspace = null) => new() { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace ?? workspaceId, Message = new ChatMessage { Role = MessageRole.User, Text = text, Time = DateTimeOffset.UtcNow } };

    [Fact]
    public async Task AFailureShowsWhenItHappenedAsTheEventSaysAndTheNewestFailureOfTheSameTextTakesOverTheTime()
    {
        await Showing();
        DateTimeOffset first = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

        connection.Raise(x => x.EventReceived += null, Failed("usage limit reached", first));

        Assert.Equal(("usage limit reached", first), (chat.Error, chat.ErrorTime));
        Assert.True(chat.HasError);

        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        connection.Raise(x => x.EventReceived += null, Failed("usage limit reached", first.AddHours(2)));

        Assert.Equal(("usage limit reached", first.AddHours(2)), (chat.Error, chat.ErrorTime));
        Assert.Contains(nameof(ChatViewModel.ErrorTime), changed);
    }

    [Fact]
    public async Task AFailureWithoutATimeIsStampedWhenItArrives()
    {
        await Showing();
        DateTimeOffset before = DateTimeOffset.UtcNow;

        connection.Raise(x => x.EventReceived += null, Failed("older server", null));

        Assert.InRange(chat.ErrorTime!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task TheNextInputOfAnyClientTakesTheErrorAwayAndItsTimeWithIt()
    {
        await Showing(Said("earlier"));
        connection.Raise(x => x.EventReceived += null, Failed("The AI could not be started", DateTimeOffset.UtcNow));
        Assert.True(chat.HasError);

        connection.Raise(x => x.EventReceived += null, Typed("try again"));

        Assert.Null(chat.Error);
        Assert.False(chat.HasError);
        Assert.Null(chat.ErrorTime);
        Assert.Equal(["earlier", "try again"], chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task TheErrorStaysWhileOnlyTheAiOrOtherWorkspacesSpeak()
    {
        await Showing();
        connection.Raise(x => x.EventReceived += null, Failed("still here", DateTimeOffset.UtcNow));

        connection.Raise(x => x.EventReceived += null, Added(Said("an answer")));
        connection.Raise(x => x.EventReceived += null, Added(Did("shell: ls")));
        connection.Raise(x => x.EventReceived += null, Typed("typed elsewhere", Guid.NewGuid()));
        connection.Raise(x => x.EventReceived += null, Failed("another workspace", DateTimeOffset.UtcNow, Guid.NewGuid()));

        Assert.Equal("still here", chat.Error);
    }

    [Fact]
    public async Task AnErrorTheClientHasItselfIsStampedWithTheTimeItHappened()
    {
        connection.Setup(x => x.SetShowWork(workspaceId, true, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The workspace is gone."));
        DateTimeOffset before = DateTimeOffset.UtcNow;

        chat.ShowWork = true;
        for (int attempt = 0; attempt < 100 && chat.Error is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.InRange(chat.ErrorTime!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    private List<ChatMessage> Numbered(int from, int to) => [.. Enumerable.Range(from, to - from).Select(number => Said($"m{number}"))];

    private async Task WithHistory(int total, int newest, bool showWork = false)
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Numbered(total - newest, total), Older = (total - newest).ToString(CultureInfo.InvariantCulture), ShowWork = showWork });
        connection.Setup(x => x.GetHistory(workspaceId, It.Is<string?>(cursor => cursor != null), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string? cursor, int? limit, int? characters, CancellationToken _) =>
        {
            int end = int.Parse(cursor!, CultureInfo.InvariantCulture);
            int begin = Math.Max(0, end - limit!.Value);
            return new ChatHistory { Messages = Numbered(begin, end), Older = begin > 0 ? begin.ToString(CultureInfo.InvariantCulture) : null };
        });
        await chat.Start();
    }

    [Fact]
    public async Task Start_LoadsOnlyOneSmallChunkOfAConversationThatIsLong()
    {
        await WithHistory(1000, 20);

        Assert.Equal(20, chat.Messages.Count);
        Assert.True(chat.HasMore);
        connection.Verify(x => x.GetHistory(workspaceId, null, 20, 20_000, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhileFollowingTheEndTheOldestMessagesAreDroppedToKeepTheChunkAndScrollingUpLoadsThemAgain()
    {
        await WithHistory(1000, 20);
        List<NotifyCollectionChangedAction> actions = [];
        chat.Messages.CollectionChanged += (_, args) => actions.Add(args.Action);

        for (int number = 1000; number < 1050; number++)
        {
            connection.Raise(x => x.EventReceived += null, Added(Said($"m{number}")));
        }

        Assert.Equal(20, chat.Messages.Count);
        Assert.Equal(["m1030", "m1049"], new[] { chat.Messages[0].Text, chat.Messages[^1].Text });
        Assert.True(chat.HasMore);

        Assert.True(await chat.LoadOlder());
        Assert.Equal(40, chat.Messages.Count);
        Assert.Equal(["m1010", "m1049"], new[] { chat.Messages[0].Text, chat.Messages[^1].Text });
        connection.Verify(x => x.GetHistory(workspaceId, "1030", 20, 20_000, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(Enumerable.Range(1010, 40).Select(number => $"m{number}"), chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task WhenAutoScrollIsOffNothingIsDroppedUntilTheViewReachesTheEndAgain()
    {
        await WithHistory(1000, 20);
        chat.AutoScroll = false;

        for (int number = 1000; number < 1015; number++)
        {
            connection.Raise(x => x.EventReceived += null, Added(Said($"m{number}")));
        }

        Assert.Equal(35, chat.Messages.Count);
        Assert.True(chat.TrimToNewest());
        Assert.Equal(20, chat.Messages.Count);
        Assert.Equal("m995", chat.Messages[0].Text);
        Assert.False(chat.TrimToNewest());
    }

    [Fact]
    public async Task OutputArrivedIsRaisedForWhatIsWrittenAndNotForOlderMessagesOrHiddenWork()
    {
        await WithHistory(1000, 20);
        int arrived = 0;
        chat.OutputArrived += () => arrived++;

        await chat.LoadOlder();
        connection.Raise(x => x.EventReceived += null, Added(Weighty("hidden", 10, true)));
        Assert.Equal(0, arrived);

        connection.Raise(x => x.EventReceived += null, Added(Said("new")));
        Assert.Equal(1, arrived);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadingAgainWhileScrolledUpKeepsWhatWasLoadedAndCountsOnlyWhatIsNewAsOutput(bool somethingNew)
    {
        await WithHistory(1000, 20);
        await chat.LoadOlder();
        chat.IsAtEnd = false;
        int newest = somethingNew ? 1001 : 1000;
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Enumerable.Range(newest - 20, 20).Select(number => Said($"m{number}")).ToList(), Older = $"{newest - 20}" });
        connection.Setup(x => x.GetHistory(workspaceId, $"{newest - 20}", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Enumerable.Range(newest - 40, 20).Select(number => Said($"m{number}")).ToList(), Older = $"{newest - 40}" });
        List<string> first = [];
        int arrived = 0;
        chat.OutputArrived += () =>
        {
            arrived++;
            first.Add(chat.Messages[0].Text);
        };

        await chat.Resync();

        // New output takes a conversation that follows its end back to the newest chunk; without any, what was loaded stays.
        Assert.Equal(somethingNew ? 1 : 0, arrived);
        Assert.Equal(somethingNew ? ("m981", 20) : ("m960", 40), (chat.Messages[0].Text, chat.Messages.Count));
        Assert.True(chat.AutoScroll);
    }

    [Fact]
    public async Task ReachingTheEndDropsTheChunksLoadedByScrollingUpAndTheyLoadAgainFromTheRightPlace()
    {
        await WithHistory(1000, 20);
        await chat.LoadOlder();
        await chat.LoadOlder();
        Assert.Equal(60, chat.Messages.Count);
        Assert.Equal("m940", chat.Messages[0].Text);
        List<NotifyCollectionChangedEventArgs> changes = [];
        chat.Messages.CollectionChanged += (_, args) => changes.Add(args);

        bool dropped = chat.TrimToNewest();

        Assert.True(dropped);
        NotifyCollectionChangedEventArgs change = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Remove, 40, 0), (change.Action, change.OldItems!.Count, change.OldStartingIndex));
        Assert.Equal(Enumerable.Range(980, 20).Select(number => $"m{number}"), chat.Messages.Select(message => message.Text));
        Assert.True(chat.HasMore);

        Assert.True(await chat.LoadOlder());
        Assert.Equal(["m960", "m999"], new[] { chat.Messages[0].Text, chat.Messages[^1].Text });
        connection.Verify(x => x.GetHistory(workspaceId, "980", 20, 20_000, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ALoadedChunkIsInsertedWithOneNotificationAndTheBeginningOfTheConversationEndsTheLoading()
    {
        await WithHistory(30, 20);
        List<NotifyCollectionChangedEventArgs> changes = [];
        chat.Messages.CollectionChanged += (_, args) => changes.Add(args);

        Assert.True(await chat.LoadOlder());

        NotifyCollectionChangedEventArgs change = Assert.Single(changes);
        Assert.Equal((NotifyCollectionChangedAction.Add, 10, 0), (change.Action, change.NewItems!.Count, change.NewStartingIndex));
        Assert.False(chat.HasMore);
        Assert.False(await chat.LoadOlder());
        Assert.Equal(30, chat.Messages.Count);
    }

    [Fact]
    public async Task TurningAutoScrollOnDropsTheChunksLoadedWhileItWasOff()
    {
        await WithHistory(1000, 20);
        chat.AutoScroll = false;
        await chat.LoadOlder();
        Assert.Equal(40, chat.Messages.Count);

        chat.AutoScroll = true;

        Assert.Equal(20, chat.Messages.Count);
        Assert.Equal("m980", chat.Messages[0].Text);
    }

    [Fact]
    public async Task AnAnswerBeingStreamedTakesTheSeatOfTheOldestMessageAndTheFinishedAnswerTakesItsOwn()
    {
        await WithHistory(1000, 20, true);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = "Hel" });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.MessageDelta, WorkspaceId = workspaceId, Text = "lo" });

        Assert.Equal((20, "m981", "Hello"), (chat.Messages.Count, chat.Messages[0].Text, chat.Messages[^1].Text));

        connection.Raise(x => x.EventReceived += null, Added(Said("Hello there")));

        Assert.Equal((20, "m981", "Hello there"), (chat.Messages.Count, chat.Messages[0].Text, chat.Messages[^1].Text));
        Assert.True(await chat.LoadOlder());
        Assert.Equal("m961", chat.Messages[0].Text);
    }

    [Fact]
    public async Task AChunkReadWhileTheWindowMovedIsDiscardedSoNothingIsMissingOrRepeated()
    {
        await WithHistory(1000, 20);
        TaskCompletionSource<ChatHistory> slow = new();
        connection.Setup(x => x.GetHistory(workspaceId, "980", 20, 20_000, It.IsAny<CancellationToken>())).Returns(slow.Task);

        Task<bool> loading = chat.LoadOlder();
        for (int number = 1000; number < 1005; number++)
        {
            connection.Raise(x => x.EventReceived += null, Added(Said($"m{number}")));
        }

        slow.SetResult(new ChatHistory { Messages = Numbered(960, 980), Older = "960" });

        Assert.False(await loading);
        Assert.Equal(Enumerable.Range(985, 20).Select(number => $"m{number}"), chat.Messages.Select(message => message.Text));

        connection.Setup(x => x.GetHistory(workspaceId, "985", 20, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Numbered(965, 985), Older = "965" });
        Assert.True(await chat.LoadOlder());
        Assert.Equal(Enumerable.Range(965, 40).Select(number => $"m{number}"), chat.Messages.Select(message => message.Text));
    }

    private LimitPause Pause(double percent = 96) => new() { Window = "5h", PercentUsed = percent, MaxPercent = 95, PausedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow.AddMinutes(15) };

    [Fact]
    public async Task LimitPause_IsReadWithTheHistoryAndFollowsTheServerAsItStartsChangesAndEnds()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        LimitPause first = Pause();
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], LimitPause = first });
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await chat.Start();

        Assert.Equal((first, true), (chat.LimitPause, chat.HasLimitPause));
        Assert.Contains("the 5h usage window is at 96%", chat.LimitPauseText);
        Assert.Contains("maximum of 95%", chat.LimitPauseText);

        LimitPause next = Pause(97.5);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspaceId, LimitPause = next });
        Assert.Contains("is at 97.5%", chat.LimitPauseText);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = Guid.NewGuid(), LimitPause = null });
        Assert.True(chat.HasLimitPause);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspaceId, LimitPause = null });

        Assert.False(chat.HasLimitPause);
        Assert.Equal(string.Empty, chat.LimitPauseText);
        Assert.Contains(nameof(ChatViewModel.HasLimitPause), changed);
        Assert.Contains(nameof(ChatViewModel.LimitPauseText), changed);
    }

    private void Refuse(string prompt) => connection.Setup(x => x.Prompt(workspaceId, prompt, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("over", Pause()));

    [Fact]
    public async Task Send_WhenTheServerRefusesBecauseOfTheMaximumKeepsTheInputAsksAndSendsItAnywayOnlyOnConfirm()
    {
        Refuse("keep going");
        chat.Input = "keep going";
        List<string?> changed = [];
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        chat.SendCommand.Execute(null);
        await Until(() => chat.IsAskingOverLimit);

        Assert.Equal("keep going", chat.Input);
        Assert.Null(chat.Error);
        Assert.Contains("5h usage window is at 96%", chat.OverLimitText);
        Assert.Contains("ignores the maximum until this work is done", chat.OverLimitText);
        connection.Verify(x => x.PromptOverLimit(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        chat.ConfirmOverLimitCommand.Execute(null);
        await Until(() => chat.Input.Length == 0);

        connection.Verify(x => x.PromptOverLimit(workspaceId, "keep going", It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(chat.IsAskingOverLimit);
        Assert.Contains(nameof(ChatViewModel.IsAskingOverLimit), changed);
    }

    [Fact]
    public async Task Send_SendsTheAttachmentsOfARefusedInputAnywayAndClearsTheBoxOnlyWhileItStillHoldsThem()
    {
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.png", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/srv/a.png");
        Refuse("look\nAttached file: /srv/a.png");
        chat.Input = "look";
        chat.Attach("a.png", new byte[] { 1 });

        chat.SendCommand.Execute(null);
        await Until(() => chat.IsAskingOverLimit && chat.Input == "look" && chat.Attachments.Count == 1);
        chat.ConfirmOverLimitCommand.Execute(null);
        await Until(() => chat.Attachments.Count == 0 && chat.Input.Length == 0);

        connection.Verify(x => x.PromptOverLimit(workspaceId, "look\nAttached file: /srv/a.png", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.UploadAttachment(workspaceId, "a.png", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancelOverLimit_DropsTheQuestionAndSendsNothing()
    {
        Refuse("keep going");
        chat.Input = "keep going";
        chat.SendCommand.Execute(null);
        await Until(() => chat.IsAskingOverLimit);

        chat.CancelOverLimitCommand.Execute(null);

        Assert.False(chat.IsAskingOverLimit);
        Assert.Equal("keep going", chat.Input);
        Assert.Equal(string.Empty, chat.OverLimitText);
        connection.Verify(x => x.PromptOverLimit(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TheButtonsThatSendAFixedPromptAskToo()
    {
        Refuse("resume unfinished work");

        chat.ResumeCommand.Execute(null);
        await Until(() => chat.IsAskingOverLimit);
        chat.ConfirmOverLimitCommand.Execute(null);
        await Until(() => connection.Invocations.Any(call => call.Method.Name == nameof(IServerConnection.PromptOverLimit)));

        connection.Verify(x => x.PromptOverLimit(workspaceId, "resume unfinished work", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARefusalThatIsNotAboutTheMaximumShowsAsAnErrorWithoutAsking()
    {
        connection.Setup(x => x.Prompt(workspaceId, "go", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The workspace is stopping."));
        chat.Input = "go";

        chat.SendCommand.Execute(null);
        await Until(() => chat.Error is not null);

        Assert.False(chat.IsAskingOverLimit);
        Assert.Equal("The workspace is stopping.", chat.Error);
    }

    [Fact]
    public async Task CancelLimitPause_AsksTheServerToEndThePauseWithoutResumingAndSaysSoUnderTheConversation()
    {
        connection.Setup(x => x.CancelLimitPause(workspaceId, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        chat.CancelLimitPauseCommand.Execute(null);
        await Until(() => chat.Notice is not null);

        connection.Verify(x => x.CancelLimitPause(workspaceId, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("automatic resume was cancelled", chat.Notice);
    }

    [Fact]
    public async Task CancelLimitPause_ShowsWhyTheServerRefused()
    {
        connection.Setup(x => x.CancelLimitPause(workspaceId, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("No such workspace."));

        chat.CancelLimitPauseCommand.Execute(null);
        await Until(() => chat.Error is not null);

        Assert.Equal("No such workspace.", chat.Error);
        Assert.Null(chat.Notice);
    }

    private ChatMessage Weighty(string name, int characters, bool work = false) => new() { Role = MessageRole.Assistant, Text = name + new string('x', characters - name.Length), IsWork = work };

    private async Task Toggled(bool turningOn)
    {
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspaceId, ShowWork = turningOn });

        for (int attempt = 0; attempt < 100 && chat.ShowWork != turningOn; attempt++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(50);
    }

    [Fact]
    public async Task TurningTheWorkOnWhileFollowingTheEndReadsOnlyTheNewestChunkWithTheWorkCountedInIt()
    {
        await WithHistory(1000, 20);
        Assert.True(chat.AutoScroll);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [Said("a"), Weighty("w1", 9000, true), Weighty("w2", 9000, true), Weighty("w3", 9000, true), Said("b")], Older = "4000", ShowWork = true });

        await Toggled(true);

        Assert.Equal(["a", "w1", "w2", "w3", "b"], chat.Messages.Select(message => message.Text.Length > 3 ? message.Text[..2] : message.Text));
        connection.Verify(x => x.GetHistory(workspaceId, It.Is<string?>(cursor => cursor != null), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(chat.HasMore);
        Assert.True(chat.TrimToNewest());
        Assert.Equal(["w2", "w3", "b"], chat.Messages.Select(message => message.Text.Length > 3 ? message.Text[..2] : message.Text));
    }

    [Fact]
    public async Task TurningTheWorkOnAfterScrollingUpReadsBackOnlyAsFarAsTheTextThatWasLoadedAndFindsTheMessageWhenItIsWithinThat()
    {
        await WithHistory(1000, 20);
        chat.AutoScroll = false;
        await chat.LoadOlder();
        string anchor = chat.Messages[0].Text;
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Enumerable.Range(1000 - 30, 30).Select(number => Said($"m{number}")).ToList(), Older = "970", ShowWork = true });
        connection.Setup(x => x.GetHistory(workspaceId, "970", It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = Enumerable.Range(950, 20).Select(number => Said($"m{number}")).ToList(), Older = "950", ShowWork = true });

        await Toggled(true);

        Assert.Equal("m960", anchor);
        Assert.Equal(("m950", "m999", false), (chat.Messages[0].Text, chat.Messages[^1].Text, chat.AutoScroll));
        Assert.Equal(50, chat.Messages.Count);
    }

    [Fact]
    public async Task TurningTheWorkOnAfterScrollingUpGivesUpAtTheTextBudgetAndFollowsTheEndInsteadOfLoadingWithoutEnd()
    {
        await WithHistory(1000, 20);
        chat.AutoScroll = false;
        await chat.LoadOlder();
        List<string?> asked = [];
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [Weighty("w0", 9000, true), Weighty("w1", 9000, true), Said("tail")], Older = "5000", ShowWork = true });
        connection.Setup(x => x.GetHistory(workspaceId, It.Is<string?>(cursor => cursor != null && cursor != "980" && cursor != "960"), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string? cursor, int? _, int? _, CancellationToken _) =>
        {
            asked.Add(cursor);
            int end = int.Parse(cursor!, CultureInfo.InvariantCulture);
            return new ChatHistory { Messages = [Weighty("w", 12000, true), Weighty("v", 12000, true)], Older = (end - 2).ToString(CultureInfo.InvariantCulture), ShowWork = true };
        });

        await Toggled(true);

        Assert.Single(asked);
        Assert.True(chat.AutoScroll);
        Assert.True(chat.Messages.Sum(message => message.Text.Length) <= 20_000 + 12_000);
        Assert.True(chat.HasMore);
    }

    [Fact]
    public async Task TurningTheWorkOffWhileFollowingTheEndReadsOnlyTheNewestChunk()
    {
        await WithHistory(1000, 20, true);
        connection.Setup(x => x.GetHistory(workspaceId, null, It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [Said("a"), Said("b")], Older = "300", ShowWork = false });

        await Toggled(false);

        Assert.Equal(["a", "b"], chat.Messages.Select(message => message.Text));
        connection.Verify(x => x.GetHistory(workspaceId, It.Is<string?>(cursor => cursor != null), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnAnswerStreamedLongEnoughToFillTheChunkDropsTheOldMessagesWhileFollowingTheEnd()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        History(true, Said("old one"), Said("old two"), Said("old three"));
        await chat.Start();

        connection.Raise(x => x.EventReceived += null, Piece("start ", false));
        connection.Raise(x => x.EventReceived += null, Piece(new string('y', 25_000), false));

        Assert.Single(chat.Messages);
        Assert.True(chat.HasMore);
        Assert.Equal(25_006, chat.Messages[0].Text.Length);
    }
}
