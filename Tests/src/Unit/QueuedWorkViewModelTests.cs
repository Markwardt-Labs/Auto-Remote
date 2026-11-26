namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class QueuedWorkViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid workspaceId = Guid.NewGuid();

    private readonly ChatViewModel chat;

    public QueuedWorkViewModelTests()
    {
        connection.Setup(x => x.GetSavedInputs(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspaceId, It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        chat = new ChatViewModel(connection.Object, workspaceId);
    }

    private void Queue(QueueStatus? status) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspaceId, Queue = status });

    private QueueStatus Waiting(string text, params string[] running) => new() { Position = 2, Text = text, Running = running };

    [Fact]
    public async Task Start_ShowsTheWorkAlreadyWaitingAndTheEventsKeepItCurrent()
    {
        connection.Setup(x => x.GetQueuedWork(workspaceId, It.IsAny<CancellationToken>())).ReturnsAsync(Waiting("fix it\nAttached file: /data/0123abcd-log.txt", "app", "web"));

        await chat.Start();

        Assert.True(chat.IsQueued);
        Assert.Equal("Queued (position 2), waiting for app, web.", chat.QueueText);
        Assert.Equal("fix it\nlog.txt", chat.QueuedPrompt);
        Assert.True(chat.HasQueuedPrompt);

        Queue(new QueueStatus { Position = 1 });

        Assert.Equal("Queued (position 1).", chat.QueueText);
        Assert.False(chat.HasQueuedPrompt);

        Queue(null);

        Assert.False(chat.IsQueued);
        Assert.Equal(string.Empty, chat.QueueText);
    }

    [Fact]
    public async Task CancellingAPromptThisClientSentPutsItBackBeforeWhatIsTypedWithItsAttachmentsAlreadyUploaded()
    {
        await chat.Start();
        connection.Setup(x => x.UploadAttachment(workspaceId, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/data/0123abcd-a.txt");
        chat.Input = "do it";
        chat.Attach("a.txt", new byte[] { 1 });
        chat.SendCommand.Execute(null);
        string queued = "do it\nAttached file: /data/0123abcd-a.txt";
        Queue(Waiting(queued));
        connection.Setup(x => x.CancelQueuedWork(workspaceId, false, It.IsAny<CancellationToken>())).ReturnsAsync(Waiting(queued));
        chat.Input = "next thing";

        chat.CancelQueuedCommand.Execute(null);

        Assert.False(chat.IsQueued);
        Assert.Equal("do it\nnext thing", chat.Input);
        ChatAttachment restored = Assert.Single(chat.Attachments);
        Assert.Equal(("a.txt", "/data/0123abcd-a.txt"), (restored.Name, restored.ServerPath));

        string? sent = null;
        connection.Setup(x => x.Prompt(workspaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback((Guid _, string text, CancellationToken _) => sent = text).Returns(Task.CompletedTask);
        chat.SendCommand.Execute(null);

        Assert.Equal("do it\nnext thing\nAttached file: /data/0123abcd-a.txt", sent);
        connection.Verify(x => x.UploadAttachment(workspaceId, It.IsAny<string>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellingAPromptSentElsewhereHasTheServerKeepItAsASavedInputAndLeavesTheBoxAlone()
    {
        await chat.Start();
        Queue(Waiting("from the phone"));
        connection.Setup(x => x.CancelQueuedWork(workspaceId, true, It.IsAny<CancellationToken>())).ReturnsAsync(Waiting("from the phone"));
        chat.Input = "mine";

        chat.CancelQueuedCommand.Execute(null);

        Assert.Equal("mine", chat.Input);
        Assert.Contains("saved input", chat.Notice);
        connection.Verify(x => x.CancelQueuedWork(workspaceId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("dropped", 1)]
    [InlineData(null, 0)]
    public async Task OnlyQueuedWorkThatWillNotRunEndsTheWaitForTheReadyNotification(string? marker, int ended)
    {
        await chat.Start();
        int failed = 0;
        chat.PromptFailed += () => failed++;
        Queue(Waiting("work"));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspaceId, Text = marker });

        Assert.Equal(ended, failed);
    }

    [Fact]
    public async Task APromptOfAnotherClientThatOnlyContainsWhatThisOneSentIsNotTakenForItsOwn()
    {
        await chat.Start();
        connection.Setup(x => x.CancelQueuedWork(workspaceId, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(Waiting("please continue the work"));
        chat.Input = "continue";
        chat.SendCommand.Execute(null);
        Queue(Waiting("please continue the work"));

        chat.CancelQueuedCommand.Execute(null);

        Assert.Equal(string.Empty, chat.Input);
        connection.Verify(x => x.CancelQueuedWork(workspaceId, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellingTheQueuedWorkOfADelegatedTaskSaysTheTaskWasCancelled()
    {
        await chat.Start();
        QueueStatus task = Waiting("the task") with { Delegated = true };
        connection.Setup(x => x.CancelQueuedWork(workspaceId, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(task);
        Queue(task);

        chat.CancelQueuedCommand.Execute(null);

        Assert.Equal(("The delegated task was cancelled.", string.Empty), (chat.Notice, chat.Input));
    }

    [Fact]
    public async Task ARefusedCancelLeavesTheWorkShownAsWaiting()
    {
        await chat.Start();
        Queue(Waiting("work"));
        connection.Setup(x => x.CancelQueuedWork(workspaceId, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The workspace is gone."));

        chat.CancelQueuedCommand.Execute(null);

        Assert.True(chat.IsQueued);
        Assert.Equal("The workspace is gone.", chat.Error);
    }

    [Fact]
    public async Task TheDrawerNotesWhichWorkspacesWaitForTheLocalAi()
    {
        WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/a" };
        WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/b" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [first, second], Queued = [second.Id] });
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        HomeViewModel home = new(connection.Object);
        await home.Start();

        Assert.Equal(["/work/a", "Queued - /work/b"], home.Entries.Select(entry => entry.Detail).Order());

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = first.Id, Queue = new QueueStatus { Position = 1 } });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = second.Id });

        Assert.Equal(["/work/b", "Queued - /work/a"], home.Entries.Select(entry => entry.Detail).Order());
    }

    [Fact]
    public async Task TheEnvironmentScreenSendsTheModeAndTheDelegatedLimitAndShowsTheLimitOnlyForSimultaneous()
    {
        List<AiEnvironment> saved = [];
        connection.Setup(x => x.GetAiEnvironment(It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment());
        connection.Setup(x => x.SetAiEnvironment(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>())).ReturnsAsync((AiEnvironment given, CancellationToken _) =>
        {
            saved.Add(given);
            return given;
        });
        EnvironmentViewModel model = new(connection.Object);
        await model.Load();

        Assert.Equal((LocalWorkMode.Yield, false), (model.SelectedWorkMode.Value, model.RunsSimultaneously));

        model.SelectedWorkMode = model.WorkModes[1];
        model.DelegatedTaskLimit = 40;
        Assert.True(model.RunsSimultaneously);
        model.CloseCommand.Execute(null);

        Assert.Equal((LocalWorkMode.Simultaneous, 16), (saved.Single().LocalWorkMode, saved.Single().DelegatedTaskLimit));
    }
}
