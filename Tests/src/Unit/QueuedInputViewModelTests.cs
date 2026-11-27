namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class QueuedInputViewModelTests
{
    public QueuedInputViewModelTests()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        model = new HomeViewModel(connection.Object, ClientLayout.Tabs, null, null, null, notifier.Object, keeper.Object);
    }

    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReadyNotifier> notifier = new();

    private readonly Mock<IWorkKeeper> keeper = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly HomeViewModel model;

    private ChatViewModel Chat => model.Active!.Chat;

    private async Task<WorkspaceViewModel> Started()
    {
        await model.Start();
        await Task.Delay(50);
        return model.Active!;
    }

    private void Raise(RemoteEvent remoteEvent) => connection.Raise(x => x.EventReceived += null, remoteEvent);

    private void Working(bool working) => Raise(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working });

    private void AutoCommit(AutoCommitState state) => Raise(new RemoteEvent { Kind = RemoteEventKind.AutoCommitChanged, WorkspaceId = workspace.Id, AutoCommit = state });

    private void Saved(params SavedInput[] inputs) => Raise(new RemoteEvent { Kind = RemoteEventKind.SavedInputsChanged, WorkspaceId = workspace.Id, SavedInputs = inputs });

    private SavedInput Input(string text, bool queued = false, bool paused = false) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = queued, IsPaused = paused };

    [Fact]
    public async Task AutoCommitButton_ShowsWhileTheAiWorksInARepositoryWithoutALoopAndTogglesOnTheServer()
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowAutoCommitButton);

        Working(true);

        Assert.True(view.ShowAutoCommitButton);
        Assert.False(Chat.IsAutoCommitOn);

        Chat.ToggleAutoCommitCommand.Execute(null);

        connection.Verify(x => x.SetAutoCommit(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        // Only what the server says turns it on.
        Assert.False(Chat.IsAutoCommitOn);
        AutoCommit(AutoCommitState.On);
        Assert.True(Chat.IsAutoCommitOn);
        Assert.StartsWith("The changes are committed", Chat.AutoCommitTip);

        Chat.ToggleAutoCommitCommand.Execute(null);

        connection.Verify(x => x.SetAutoCommit(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);

        LoopInfo loop = new() { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch, IsRunning = true };
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = loop });

        Assert.False(view.ShowAutoCommitButton);

        // Between the runs, other work of a looping workspace can be committed.
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = loop with { IsRunning = false } });

        Assert.True(view.ShowAutoCommitButton);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, null, false)]
    public async Task QueueTip_MentionsACommitOnlyWhenOneFollows(bool isRepo, bool? loaded, bool mentioned)
    {
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = isRepo, Branch = "main" });
        WorkspaceViewModel view = await Started();

        if (loaded is { } commits)
        {
            await Load(Input("saved") with { AutoCommit = commits });
        }
        else
        {
            Chat.Input = "typed";
        }

        Assert.Equal(mentioned, view.QueueTip.Contains("auto commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoopButton_StaysOfferedWhileTheChangesAreCommittedAfterWork()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        AutoCommit(AutoCommitState.Committing);
        Working(false);

        Assert.True(view.ShowLoopButton);

        Chat.Input = "typed";

        Assert.False(view.ShowLoopButton);
    }

    private async Task Load(SavedInput saved)
    {
        connection.Setup(x => x.TakeSavedInput(workspace.Id, saved.Id, It.IsAny<CancellationToken>())).ReturnsAsync(saved with { IsQueued = false, IsPaused = false });
        Saved(saved);
        Chat.LoadSavedInputCommand.Execute(saved);
        await Task.Delay(50);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadedInput_TakesItsAutoCommitWithItAndShowsItAboveTheMessageBox(bool commits)
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowLoadedAutoCommit);

        await Load(Input("loaded") with { AutoCommit = commits });

        Assert.True(view.ShowLoadedAutoCommit);
        Assert.Equal(commits, Chat.IsLoadedAutoCommitOn);
        Assert.Equal(commits ? "Auto Commit This Input: On" : "Auto Commit This Input: Off", Chat.LoadedAutoCommitText);

        Chat.ToggleLoadedAutoCommitCommand.Execute(null);

        Assert.Equal(!commits, Chat.IsLoadedAutoCommitOn);

        // Editing keeps it, and emptying the box lets it go.
        Chat.Input = "loaded and edited";
        Assert.True(view.ShowLoadedAutoCommit);
        Chat.Input = string.Empty;
        Assert.False(view.ShowLoadedAutoCommit);
        Chat.Input = "typed anew";
        Assert.Null(Chat.LoadedAutoCommit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadedInput_IsSentWithAutoCommitTurnedOnOnlyWhenItsOwnIsOn(bool commits)
    {
        await Started();
        await Load(Input("loaded") with { AutoCommit = commits });

        Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.PromptToCommit(workspace.Id, "loaded", false, It.IsAny<CancellationToken>()), commits ? Times.Once() : Times.Never());
        connection.Verify(x => x.Prompt(workspace.Id, "loaded", It.IsAny<CancellationToken>()), commits ? Times.Never() : Times.Once());
        Assert.Null(Chat.LoadedAutoCommit);
    }

    [Fact]
    public async Task TypedInput_IsSentAsEverAndQueuedOrSavedWithAutoCommitOn()
    {
        await Started();
        Chat.Input = "typed";
        Chat.SendCommand.Execute(null);
        Chat.Input = "queued";
        Chat.QueueCommand.Execute(null);
        connection.Setup(x => x.SaveInput(workspace.Id, "saved", true, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Chat.Input = "saved";
        Chat.SaveInputCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.Prompt(workspace.Id, "typed", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.PromptToCommit(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.QueueInput(workspace.Id, "queued", true, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SaveInput(workspace.Id, "saved", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadedInput_KeepsItsAutoCommitWhenQueuedOrSavedAgain()
    {
        await Started();
        await Load(Input("one") with { AutoCommit = false });
        Chat.QueueCommand.Execute(null);
        await Task.Delay(50);
        await Load(Input("two") with { AutoCommit = false });
        connection.Setup(x => x.SaveInput(workspace.Id, "two", false, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Chat.SaveInputCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.QueueInput(workspace.Id, "one", false, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SaveInput(workspace.Id, "two", false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(Chat.LoadedAutoCommit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadedInput_ThatCouldNotBeSentComesBackWithItsAutoCommit(bool queued)
    {
        await Started();
        connection.Setup(x => x.PromptToCommit(workspace.Id, "loaded", false, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        connection.Setup(x => x.QueueInput(workspace.Id, "loaded", true, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        await Load(Input("loaded"));

        (queued ? Chat.QueueCommand : Chat.SendCommand).Execute(null);
        await Task.Delay(50);

        Assert.Equal("loaded", Chat.Input);
        Assert.True(Chat.LoadedAutoCommit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadedInput_ComesBackWithItsAutoCommitAfterARestart(bool commits)
    {
        Mock<IDraftStore> drafts = new();
        drafts.Setup(x => x.Load(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync("loaded before");
        drafts.Setup(x => x.LoadAutoCommit(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(commits);
        ChatViewModel chat = new(connection.Object, workspace.Id, drafts.Object);

        await chat.Start();
        await Task.Delay(50);

        Assert.Equal(("loaded before", (bool?)commits), (chat.Input, chat.LoadedAutoCommit));

        chat.Input = string.Empty;

        drafts.Verify(x => x.SaveAutoCommit(workspace.Id, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueCounts_FeedTheBadgeAndTheBannerOfAPausedQueue()
    {
        await Started();

        Saved(Input("a", true), Input("b", true), Input("c"));

        Assert.Equal((2, true, 2, false), (Chat.QueuedCount, Chat.HasQueuedInputs, Chat.ActiveQueuedCount, Chat.HasPausedQueued));

        Saved(Input("a", true, true), Input("b", true, true));

        Assert.Equal((2, 0, true), (Chat.QueuedCount, Chat.ActiveQueuedCount, Chat.HasPausedQueued));
        Assert.Equal("Queue paused (2)", Chat.QueuePausedText);

        Saved(Input("c"));

        Assert.Equal((0, false, false), (Chat.QueuedCount, Chat.HasQueuedInputs, Chat.HasPausedQueued));
    }

    [Fact]
    public async Task ResumeQueue_AsksTheServerAndWaitsForTheWorkLikeQueuingDoes()
    {
        WorkspaceViewModel view = await Started();
        Saved(Input("a", true, true));

        Chat.ResumeQueueCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.ResumeQueue(workspace.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(view.NotifyWhenReady);
        keeper.Verify(x => x.Hold(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task ResumeQueue_ThatIsRefusedWaitsForNothing()
    {
        WorkspaceViewModel view = await Started();
        connection.Setup(x => x.ResumeQueue(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));

        Chat.ResumeQueueCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(view.NotifyWhenReady);
        Assert.Equal("refused", Chat.Error);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "Queuing or saving it keeps its own setting: off.")]
    public async Task LoadedInputButton_SaysAutoCommitIsAlreadyOnForTheCurrentWorkAndCannotBePressedThen(bool commits, string? note)
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        await Load(Input("loaded") with { AutoCommit = commits });
        Assert.False(view.HasLoadedAutoCommitNote);

        AutoCommit(AutoCommitState.On);

        Assert.Equal("Auto Commit Already On for Current Work", Chat.LoadedAutoCommitText);
        Assert.True(Chat.ShowsLoadedAutoCommitOn);
        Assert.False(Chat.ToggleLoadedAutoCommitCommand.CanExecute(null));
        Assert.Equal(note, view.LoadedAutoCommitNote);
        // Both buttons show, so the one beside the message box says what it is about.
        Assert.True(view.LabelsAutoCommitButton);
        Assert.Equal(commits, Chat.LoadedAutoCommit);

        AutoCommit(AutoCommitState.Off);

        Assert.True(Chat.ToggleLoadedAutoCommitCommand.CanExecute(null));
        Assert.Equal(commits, Chat.ShowsLoadedAutoCommitOn);
        Assert.Null(view.LoadedAutoCommitNote);
    }

    [Fact]
    public async Task AutoCommitButton_IsOnlyLabelledWhileTheButtonAboutALoadedInputShowsToo()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        Assert.False(view.LabelsAutoCommitButton);
        Chat.Input = "typed";
        Assert.False(view.LabelsAutoCommitButton);
        Chat.Input = string.Empty;

        await Load(Input("loaded"));

        Assert.True(view.LabelsAutoCommitButton);

        Working(false);

        Assert.False(view.LabelsAutoCommitButton);
    }

    private void Looping(bool running) => Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch, IsRunning = running } });

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task LoadedInput_ThatWouldJoinARunOfALoopSaysTheRunIsNotCommitted(bool commits, bool warned)
    {
        WorkspaceViewModel view = await Started();
        Looping(true);
        Working(true);

        await Load(Input("loaded") with { AutoCommit = commits });

        Assert.Equal(warned ? "Sent now, it joins the loop's run, which is not committed. Queue it to have it committed." : null, view.LoadedAutoCommitNote);

        Looping(false);

        Assert.Null(view.LoadedAutoCommitNote);
    }

    [Theory]
    [InlineData(false, 2, false, AutoCommitState.Off, "Loop waiting for 2 queued inputs.")]
    [InlineData(false, 1, false, AutoCommitState.Off, "Loop waiting for 1 queued input.")]
    [InlineData(false, 2, true, AutoCommitState.On, "Loop waiting for the current work and 2 queued inputs.")]
    [InlineData(false, 0, true, AutoCommitState.Off, "Loop waiting for the current work to finish.")]
    [InlineData(false, 0, false, AutoCommitState.Committing, "Loop waiting for the commit.")]
    [InlineData(false, 1, false, AutoCommitState.Committing, "Loop waiting for the commit and 1 queued input.")]
    [InlineData(true, 2, true, AutoCommitState.Off, "2 queued inputs will run when this loop run ends.")]
    [InlineData(true, 0, true, AutoCommitState.Off, null)]
    [InlineData(false, 0, false, AutoCommitState.Off, null)]
    public async Task LoopWaiting_SaysWhatGoesBeforeTheNextRun(bool running, int queued, bool working, AutoCommitState autoCommit, string? expected)
    {
        WorkspaceViewModel view = await Started();
        Looping(running);
        Working(working);
        AutoCommit(autoCommit);

        Saved([.. Enumerable.Range(0, queued).Select(index => Input($"q{index}", true)), Input("paused does not count")]);

        Assert.Equal(expected, view.LoopWaiting);
        Assert.Equal(expected is not null, view.IsLoopWaiting);

        if (!running && (queued > 0 || autoCommit == AutoCommitState.Committing))
        {
            Assert.Equal(expected, view.LoopNext);
        }
    }

    [Theory]
    [InlineData(2, "2 queued inputs go before the loop's next run.")]
    [InlineData(1, "1 queued input goes before the loop's next run.")]
    [InlineData(0, null)]
    public async Task LoopWaiting_DoesNotSayTheLoopWaitsBeforeItsNextRunIsDue(int queued, string? expected)
    {
        WorkspaceViewModel view = await Started();
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", IntervalSeconds = 600 }, StartedAt = DateTimeOffset.UtcNow, Runs = 1, NextRunAt = DateTimeOffset.UtcNow.AddMinutes(9) } });
        Working(true);

        Saved([.. Enumerable.Range(0, queued).Select(index => Input($"q{index}", true))]);

        Assert.Equal(expected, view.LoopWaiting);
        // The loop's icon goes on counting down.
        Assert.StartsWith("The next prompt is sent in", view.LoopNext);
    }

    [Fact]
    public async Task LoopWaiting_IsSilentWithoutALoopOrForAPausedQueueWhichTheLoopDoesNotWaitFor()
    {
        WorkspaceViewModel view = await Started();
        Saved(Input("a", true));
        Assert.Null(view.LoopWaiting);
        Looping(false);
        List<string?> changes = [];
        view.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        Saved(Input("a", true, true));

        Assert.Null(view.LoopWaiting);
        Assert.Contains(nameof(WorkspaceViewModel.LoopWaiting), changes);
    }

    [Fact]
    public async Task SavedInput_AutoCommitIsToggledOnTheServer()
    {
        await Started();
        SavedInput saved = Input("saved");
        Saved(saved);

        Chat.ToggleSavedInputAutoCommitCommand.Execute(saved);

        connection.Verify(x => x.SetSavedInputAutoCommit(workspace.Id, saved.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AutoCommitButton_StaysWhileAutoCommitWaitsAndIsNotOfferedOutsideARepository()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], AutoCommit = AutoCommitState.On });
        WorkspaceViewModel view = await Started();

        Assert.True(view.ShowAutoCommitButton);

        AutoCommit(AutoCommitState.Off);

        Assert.False(view.ShowAutoCommitButton);

        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        await view.Git.Refresh();
        Working(true);

        Assert.False(view.ShowAutoCommitButton);
    }

    [Fact]
    public async Task Committing_CountsAsGitWorkingFromBeforeTheAiSaysItStoppedUntilTheCommitIsDone()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        AutoCommit(AutoCommitState.On);
        List<WorkspaceState> states = [];
        view.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WorkspaceViewModel.State))
            {
                states.Add(view.State);
            }
        };

        AutoCommit(AutoCommitState.Committing);
        Working(false);

        Assert.Equal(WorkspaceState.Managing, view.State);
        Assert.DoesNotContain(WorkspaceState.Ready, states);
        Assert.Equal("Committing the changes, queue what comes next", Chat.Placeholder);
        Assert.False(view.ShowAutoCommitButton);
        Chat.Input = "after the commit";
        Assert.True(view.ShowQueueButton);
        Assert.True(Chat.QueueCommand.CanExecute(null));
        Assert.False(Chat.SendCommand.CanExecute(null));

        AutoCommit(AutoCommitState.Off);

        Assert.Equal(WorkspaceState.Ready, view.State);
    }

    [Fact]
    public async Task QueueButton_ShowsOnceSomethingIsTypedAndQueuesItWithItsAttachments()
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowQueueButton);
        connection.Setup(x => x.UploadAttachment(workspace.Id, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/data/0123abcd-a.txt");
        connection.Setup(x => x.QueueInput(workspace.Id, It.IsAny<string>(), true, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Chat.Input = "  do it  ";
        Chat.Attach("a.txt", new byte[] { 1 });
        Assert.True(view.ShowQueueButton);

        Chat.QueueCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.QueueInput(workspace.Id, "do it\nAttached file: /data/0123abcd-a.txt", true, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(string.Empty, Chat.Input);
        Assert.Empty(Chat.Attachments);
        // It is not part of the chat until the server sends it.
        Assert.Empty(Chat.Messages);
        Assert.True(view.NotifyWhenReady);
        keeper.Verify(x => x.Hold(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task QueueButton_GivesBackWhatCouldNotBeQueued()
    {
        WorkspaceViewModel view = await Started();
        connection.Setup(x => x.QueueInput(workspace.Id, It.IsAny<string>(), true, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        Chat.Input = "do it";

        Chat.QueueCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal("do it", Chat.Input);
        Assert.Equal("refused", Chat.Error);
        Assert.False(view.NotifyWhenReady);
        keeper.Verify(x => x.Release(workspace.Id), Times.Once);
    }

    [Fact]
    public async Task Delete_AsksFirstAndDeletesOnlyWhenConfirmed()
    {
        await Started();
        SavedInput first = Input("first", true);
        SavedInput second = Input("second");
        Saved(first, second);
        connection.Setup(x => x.DiscardSavedInput(workspace.Id, second.Id, It.IsAny<CancellationToken>())).ReturnsAsync([first]);

        Chat.AskDeleteSavedInputCommand.Execute(first);

        Assert.True(Chat.IsAskingDelete);
        Assert.Equal(("Delete this queued input? It will not be sent.", "first"), (Chat.DeleteQuestion, Chat.DeletePreview));

        Chat.CancelDeleteSavedInputCommand.Execute(null);

        Assert.False(Chat.IsAskingDelete);
        connection.Verify(x => x.DiscardSavedInput(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Chat.AskDeleteSavedInputCommand.Execute(second);
        Assert.Equal("Delete this saved input? This cannot be undone.", Chat.DeleteQuestion);
        Chat.ConfirmDeleteSavedInputCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.DiscardSavedInput(workspace.Id, second.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["first"], Chat.SavedInputs.Select(input => input.Text).ToList());
    }

    [Fact]
    public async Task Delete_DropsItsQuestionOnceTheInputIsGoneAnyway()
    {
        await Started();
        SavedInput queued = Input("first", true);
        Saved(queued);
        Chat.AskDeleteSavedInputCommand.Execute(queued);

        Saved();

        Assert.False(Chat.IsAskingDelete);
    }

    [Fact]
    public async Task QueuedInputs_AreToldApartByWhetherTheyArePaused()
    {
        await Started();

        Saved(Input("a", true), Input("b"));
        Assert.True(Chat.HasActiveQueued);
        Assert.False(Chat.HasPausedQueued);
        Assert.Equal("Queued", Chat.SavedInputs[0].QueueLabel);

        Saved(Input("a", true, true));
        Assert.False(Chat.HasActiveQueued);
        Assert.True(Chat.HasPausedQueued);
        Assert.Equal("Queued, paused", Chat.SavedInputs[0].QueueLabel);
    }

    [Fact]
    public async Task Notification_WaitsUntilTheQueuedInputsHaveAllBeenWorkedOn()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Saved(Input("next", true));

        Working(false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.True(view.NotifyWhenReady);

        // The queued input starts, and only then leaves the list.
        Working(true);
        Saved();
        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Working(false);

        notifier.Verify(x => x.Notify(workspace.Id, "app"), Times.Once);
        keeper.Verify(x => x.Release(workspace.Id), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Notification_IsGivenWhenTheQueueIsPausedOrEmptiedWhileTheWorkspaceIsReady(bool paused)
    {
        await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        SavedInput queued = Input("next", true);
        Saved(queued);
        Working(false);

        Saved(paused ? [queued with { IsPaused = true }] : []);

        notifier.Verify(x => x.Notify(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task NotifyButton_ShowsWhileQueuedInputsWaitToBeSent()
    {
        await Started();
        Assert.False(model.CanNotify);

        Saved(Input("next", true));

        Assert.True(model.CanNotify);
    }
}
