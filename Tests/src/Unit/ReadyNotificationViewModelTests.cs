namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ReadyNotificationViewModelTests
{
    public ReadyNotificationViewModelTests()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        Stored(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        model = new HomeViewModel(connection.Object, ClientLayout.Tabs, null, null, null, notifier.Object, keeper.Object);
    }

    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReadyNotifier> notifier = new();

    private readonly Mock<IWorkKeeper> keeper = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly HomeViewModel model;

    private ChatViewModel Chat => model.Active!.Chat;

    private void Stored(ChatHistory history, params SavedInput[] saved)
    {
        connection.Setup(x => x.GetHistory(workspace.Id, It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(history);
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(saved);
    }

    private async Task<WorkspaceViewModel> Started()
    {
        await model.Start();
        await Task.Delay(50);
        return model.Active!;
    }

    private async Task Reconnect()
    {
        connection.Raise(x => x.Reconnected += null);
        await Task.Delay(100);
    }

    private void Raise(RemoteEvent remoteEvent) => connection.Raise(x => x.EventReceived += null, remoteEvent);

    private void Working(bool working) => Raise(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working });

    private void AutoCommit(AutoCommitState state) => Raise(new RemoteEvent { Kind = RemoteEventKind.AutoCommitChanged, WorkspaceId = workspace.Id, AutoCommit = state });

    private void Saved(params SavedInput[] inputs) => Raise(new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspace.Id, SavedInputs = inputs });

    private void Queued(QueueStatus? status, string? text = null) => Raise(new RemoteEvent { Kind = RemoteEventKind.QueueChanged, WorkspaceId = workspace.Id, Queue = status, Text = text });

    private void Looping(bool looping) => Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = looping ? new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch } : null });

    private SavedInput Input(string text, bool queued = false) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = queued };

    private void NeverNotified() => notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);

    private async Task Send(string text)
    {
        Chat.Input = text;
        Chat.SendCommand.Execute(null);
        await Task.Delay(50);
    }

    private void Refuse() => connection.Setup(x => x.Prompt(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));

    // A queued input of the local AI starts to wait there before it leaves the list.
    private async Task<WorkspaceViewModel> WaitingForTheLocalAi()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Saved(Input("next", true));
        Working(false);
        Queued(new QueueStatus { Position = 1, Text = "next" });
        Saved();
        return view;
    }

    [Fact]
    public async Task Notification_WaitsWhileAQueuedInputWaitsForTheLocalAi()
    {
        WorkspaceViewModel view = await WaitingForTheLocalAi();

        NeverNotified();
        Assert.True(view.NotifyWhenReady);

        Queued(null);
        NeverNotified();
        Working(true);
        Working(false);

        notifier.Verify(x => x.Notify(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task DroppedWaitingWork_WithNothingElseToComeTurnsTheNotificationOff()
    {
        WorkspaceViewModel view = await WaitingForTheLocalAi();

        Queued(null, "dropped");

        Assert.False(view.NotifyWhenReady);
        keeper.Verify(x => x.Release(workspace.Id), Times.Once);
        NeverNotified();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DroppedWaitingWork_KeepsTheNotificationWhileALoopOrQueuedInputsAreStillToCome(bool loop)
    {
        WorkspaceViewModel view = await Started();

        if (loop)
        {
            Looping(true);
        }
        else
        {
            Saved(Input("later", true));
        }

        // It was off before the prompt was sent, which does not matter once the prompt was taken to wait.
        await Send("do it");
        Queued(new QueueStatus { Position = 1, Text = "do it" });

        Queued(null, "dropped");

        Assert.True(view.NotifyWhenReady);
        NeverNotified();
    }

    [Fact]
    public async Task DroppedWaitingWork_DoesNotTurnOnANotificationThatWasTurnedOff()
    {
        WorkspaceViewModel view = await Started();
        Looping(true);
        await Send("do it");
        Queued(new QueueStatus { Position = 1, Text = "do it" });
        model.ToggleNotifyCommand.Execute(null);

        Queued(null, "dropped");

        Assert.False(view.NotifyWhenReady);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSend_BesideRunningWorkPutsTheEarlierSettingBack(bool wasOn)
    {
        WorkspaceViewModel view = await Started();
        Working(true);

        if (wasOn)
        {
            model.ToggleNotifyCommand.Execute(null);
        }

        Refuse();

        await Send("do it");

        Assert.Equal(wasOn, view.NotifyWhenReady);
    }

    [Fact]
    public async Task FailedSend_WithNothingToComeTurnsTheNotificationOff()
    {
        WorkspaceViewModel view = await Started();
        view.NotifyWhenReady = true;
        Refuse();

        await Send("do it");

        Assert.False(view.NotifyWhenReady);
    }

    [Fact]
    public async Task Reload_ThatFindsQueuedInputsStillToBeSentDoesNotAnnounceReady()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Stored(new ChatHistory { Messages = [] }, Input("next", true));

        await Reconnect();

        Assert.Equal(WorkspaceState.Ready, view.State);
        Assert.True(view.NotifyWhenReady);
        NeverNotified();
    }

    [Fact]
    public async Task Reload_ThatFindsTheLastQueuedInputAtWorkDoesNotAnnounceReady()
    {
        WorkspaceViewModel view = await Started();
        Saved(Input("next", true));
        model.ToggleNotifyCommand.Execute(null);
        Stored(new ChatHistory { Messages = [], Working = true });

        await Reconnect();

        Assert.Equal(WorkspaceState.Working, view.State);
        Assert.True(view.NotifyWhenReady);
        NeverNotified();
    }

    [Fact]
    public async Task Reload_ThatFindsWorkWaitingForTheLocalAiDoesNotAnnounceReady()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        connection.Setup(x => x.GetQueuedWork(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new QueueStatus { Position = 1 });

        await Reconnect();

        Assert.True(view.NotifyWhenReady);
        NeverNotified();
    }

    [Fact]
    public async Task Reload_FromACommitToWorkNeverPassesThroughReady()
    {
        WorkspaceViewModel view = await Started();
        AutoCommit(AutoCommitState.Committing);
        model.ToggleNotifyCommand.Execute(null);
        List<WorkspaceState> states = [];
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.State))
            {
                states.Add(view.State);
            }
        };
        Stored(new ChatHistory { Messages = [], Working = true, AutoCommit = AutoCommitState.Off });

        await Reconnect();

        Assert.Equal(WorkspaceState.Working, view.State);
        Assert.DoesNotContain(WorkspaceState.Ready, states);
        NeverNotified();
    }

    [Fact]
    public async Task GitActions_AreLockedFromTheStartOfAnAutoCommit()
    {
        WorkspaceViewModel view = await Started();
        Assert.True(view.Git.CanAct);

        AutoCommit(AutoCommitState.Committing);

        Assert.True(view.Git.IsLocked);
        Assert.False(view.Git.CanAct);

        AutoCommit(AutoCommitState.Off);

        Assert.True(view.Git.CanAct);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Restore_OfAnEmptyDraftForgetsThatItHeldALoadedInput(string draft)
    {
        Mock<IDraftStore> drafts = new();
        drafts.Setup(x => x.Load(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        drafts.Setup(x => x.LoadAutoCommit(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        ChatViewModel chat = new(connection.Object, workspace.Id, drafts.Object);

        await chat.Start();
        chat.Input = "typed anew";

        Assert.Null(chat.LoadedAutoCommit);
        drafts.Verify(x => x.SaveAutoCommit(workspace.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadedInput_KeepsItsAutoCommitWhileOnlyAnAttachmentIsLeftOfTheBox()
    {
        await Started();
        SavedInput saved = Input("loaded");
        connection.Setup(x => x.TakeSavedInput(workspace.Id, saved.Id, It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        Chat.LoadSavedInputCommand.Execute(saved);
        await Task.Delay(50);
        Chat.Attach("a.txt", new byte[] { 1 });

        Chat.Input = string.Empty;

        Assert.True(Chat.HasLoadedInput);
    }

    [Fact]
    public async Task Delete_OfAnotherInputGoesThroughWhileOneIsStillBeingDeleted()
    {
        await Started();
        SavedInput first = Input("first");
        SavedInput second = Input("second");
        Saved(first, second);
        TaskCompletionSource<IReadOnlyList<SavedInput>> pending = new();
        connection.Setup(x => x.DiscardSavedInput(workspace.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(pending.Task);

        foreach (SavedInput entry in new[] { first, second, first })
        {
            Chat.AskDeleteSavedInputCommand.Execute(entry);
            Chat.ConfirmDeleteSavedInputCommand.Execute(null);
        }

        pending.SetResult([]);
        await Task.Delay(50);

        connection.Verify(x => x.DiscardSavedInput(workspace.Id, first.Id, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.DiscardSavedInput(workspace.Id, second.Id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
