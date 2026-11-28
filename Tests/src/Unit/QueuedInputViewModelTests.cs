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

    private SavedInput Input(string text, bool queued = false) => new() { Id = Guid.NewGuid(), Text = text, Time = DateTimeOffset.UtcNow, IsQueued = queued };

    private void Told(Agenda agenda) => Raise(new RemoteEvent { Kind = RemoteEventKind.AgendaChanged, WorkspaceId = workspace.Id, Agenda = agenda });

    private AgendaItem Item(AgendaKind kind, string title, Guid? id = null, bool? commit = null) => new() { Kind = kind, Title = title, Id = id, AutoCommit = commit };

    private Agenda Pending(params AgendaItem[] items) => new() { Items = items };

    [Fact]
    public async Task CommitButton_ShowsWhileTheAiWorksLoopRunsIncludedAndTogglesOnTheServer()
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowAutoCommitButton);

        Working(true);

        Assert.True(view.ShowAutoCommitButton);
        Assert.Equal((false, "When Done: Keep Changes"), (Chat.IsAutoCommitOn, Chat.AutoCommitText));

        Chat.ToggleAutoCommitCommand.Execute(null);

        connection.Verify(x => x.SetAutoCommit(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        // Only what the server says turns it on.
        Assert.False(Chat.IsAutoCommitOn);
        AutoCommit(AutoCommitState.On);
        Assert.Equal((true, "When Done: Commit"), (Chat.IsAutoCommitOn, Chat.AutoCommitText));
        Assert.StartsWith("The changes are committed", Chat.AutoCommitTip);

        Chat.ToggleAutoCommitCommand.Execute(null);

        connection.Verify(x => x.SetAutoCommit(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);

        LoopInfo loop = new() { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch, IsRunning = true };
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = loop });

        Assert.True(view.ShowAutoCommitButton);
    }

    [Fact]
    public async Task JoinNote_ShowsOnlyWhileTheAiWorksAndSomethingIsTyped()
    {
        WorkspaceViewModel view = await Started();
        Chat.Input = "typed";
        Assert.False(view.ShowJoinNote);

        Working(true);
        Assert.True(view.ShowJoinNote);

        Chat.Input = string.Empty;
        Assert.False(view.ShowJoinNote);
    }

    private async Task Load(SavedInput saved)
    {
        connection.Setup(x => x.TakeSavedInput(workspace.Id, saved.Id, It.IsAny<CancellationToken>())).ReturnsAsync(saved with { IsQueued = false });
        Saved(saved);
        Chat.LoadSavedInputCommand.Execute(saved);
        await Task.Delay(50);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LoadedInput_KeepsItsCommitSettingForQueuingWhileEditedAndLosesItOnceTheBoxIsEmptied(bool commits)
    {
        await Started();

        await Load(Input("loaded") with { AutoCommit = commits });
        Chat.Input = "loaded and edited";

        Assert.Equal((bool?)commits, Chat.LoadedAutoCommit);

        Chat.Input = string.Empty;
        Chat.Input = "typed anew";

        Assert.Null(Chat.LoadedAutoCommit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Send_NeverTurnsOnAutoCommitAlsoForALoadedInput(bool commits)
    {
        await Started();
        await Load(Input("loaded") with { AutoCommit = commits });

        Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.Prompt(workspace.Id, "loaded", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SetAutoCommit(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
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
        connection.Setup(x => x.Prompt(workspace.Id, "loaded", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
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
    public async Task Drafts_AreTheInputsThatAreNotQueuedAndTheQueuedOnesAreCountedBesideThem()
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowUpNextButton);

        Saved(Input("a", true), Input("b", true), Input("c"));

        Assert.Equal(["c"], Chat.Drafts.Select(draft => draft.Text));
        Assert.Equal((2, true, true, false), (Chat.QueuedCount, view.ShowUpNextButton, Chat.HasDrafts, Chat.HasNoDrafts));

        Chat.Input = "typed";
        Assert.False(view.ShowUpNextButton);
        Chat.Input = string.Empty;

        Saved(Input("a", true));

        Assert.Equal((false, true), (Chat.HasDrafts, Chat.HasNoDrafts));
    }

    [Fact]
    public async Task UpNextAndDrafts_ShowOneAtATime()
    {
        await Started();

        Chat.ToggleSavedInputsCommand.Execute(null);
        Chat.ToggleUpNextCommand.Execute(null);
        Assert.Equal((false, true), (Chat.IsSavedInputsOpen, Chat.IsUpNextOpen));

        Chat.ToggleSavedInputsCommand.Execute(null);
        Assert.Equal((true, false), (Chat.IsSavedInputsOpen, Chat.IsUpNextOpen));
    }

    [Fact]
    public async Task Agenda_IsReadOnOpeningAndFollowedAsItChanges()
    {
        connection.Setup(x => x.GetAgenda(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Pending(Item(AgendaKind.Queued, "first", Guid.NewGuid(), true)) with { Status = "1 up next" });
        await Started();

        Assert.Equal(("1 up next", true, false), (Chat.StatusText, Chat.HasStatusLine, Chat.HasStatusButtons));
        Assert.Equal(["first"], Chat.UpNext.Select(row => row.Title));

        Told(new Agenda());

        Assert.Equal((false, true), (Chat.HasStatusLine, Chat.HasNoUpNext));
    }

    [Fact]
    public async Task Agenda_IsReadAgainAfterAReconnect()
    {
        await Started();
        connection.Setup(x => x.GetAgenda(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new Agenda { Status = "Stopped", IsStopped = true });

        connection.Raise(x => x.Reconnected += null);
        await Task.Delay(100);

        Assert.Equal(("Stopped", true), (Chat.StatusText, Chat.IsStopped));
    }

    [Fact]
    public async Task StatusLine_OffersWhatAppliesAndSaysItItselfWhileTheServerHasNotSaid()
    {
        await Started();
        Assert.False(Chat.HasStatusLine);

        Told(new Agenda { IsStopped = true });
        Assert.Equal(("Stopped", true, true), (Chat.StatusText, Chat.HasStatusLine, Chat.HasStatusButtons));

        Told(new Agenda());
        Raise(new RemoteEvent { Kind = RemoteEventKind.LimitPauseChanged, WorkspaceId = workspace.Id, LimitPause = new LimitPause { Window = "Week", PercentUsed = 96, MaxPercent = 95, PausedAt = DateTimeOffset.UtcNow, NextCheckAt = DateTimeOffset.UtcNow } });
        Assert.Equal(("Paused at the usage limit", true), (Chat.StatusText, Chat.HasStatusButtons));
    }

    [Fact]
    public async Task Resume_AsksTheServerAndWaitsForTheWorkLikeQueuingDoes()
    {
        WorkspaceViewModel view = await Started();
        Told(new Agenda { IsStopped = true, Status = "Stopped · 1 up next" });

        Chat.ResumeAutomationCommand.Execute(null);
        await Task.Delay(50);

        connection.Verify(x => x.ResumeAutomation(workspace.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(view.NotifyWhenReady);
        keeper.Verify(x => x.Hold(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task Resume_ThatIsRefusedWaitsForNothing()
    {
        WorkspaceViewModel view = await Started();
        connection.Setup(x => x.ResumeAutomation(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));

        Chat.ResumeAutomationCommand.Execute(null);
        await Task.Delay(50);

        Assert.False(view.NotifyWhenReady);
        Assert.Equal("refused", Chat.Error);
    }

    [Fact]
    public async Task UpNextRows_SayWhyAndWhenTheyWaitAndOfferWhatTheirKindAllows()
    {
        await Started();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Told(Pending(
            Item(AgendaKind.Retry, "again") with { Waiting = "usage limit reached", At = now },
            Item(AgendaKind.BringIn, "Bring in fix", Guid.NewGuid(), true),
            Item(AgendaKind.Queued, "next", Guid.NewGuid(), false),
            Item(AgendaKind.Loop, "Loop: check")));

        Assert.Equal($"usage limit reached - at {now.ToStamp(now)}", Chat.UpNext[0].Describe(now));
        Assert.Equal([(false, false, false), (false, true, true), (true, true, false), (false, false, false)], Chat.UpNext.Select(row => (row.IsQueued, row.HasCommitChoice, row.IsCommitOn)));
    }

    [Fact]
    public async Task UpNextCommitToggle_TellsTheQueuedInputOrTheBringIn()
    {
        await Started();
        Guid bringIn = Guid.NewGuid();
        Guid queued = Guid.NewGuid();
        Told(Pending(Item(AgendaKind.BringIn, "Bring in fix", bringIn, true), Item(AgendaKind.Queued, "next", queued, false)));

        Chat.ToggleAgendaCommitCommand.Execute(Chat.UpNext[0]);
        Chat.ToggleAgendaCommitCommand.Execute(Chat.UpNext[1]);

        connection.Verify(x => x.SetBringInCommit(workspace.Id, bringIn, false, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SetSavedInputAutoCommit(workspace.Id, queued, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpNext_EditTakesTheQueuedInputIntoTheMessageBoxAndDeleteAsksFirst()
    {
        await Started();
        SavedInput edited = Input("edit me", true) with { AutoCommit = false };
        SavedInput deleted = Input("delete me", true);
        Saved(edited, deleted);
        Told(Pending(Item(AgendaKind.Queued, "edit me", edited.Id, false), Item(AgendaKind.Queued, "delete me", deleted.Id, true)));
        connection.Setup(x => x.TakeSavedInput(workspace.Id, edited.Id, It.IsAny<CancellationToken>())).ReturnsAsync(edited with { IsQueued = false });
        Chat.ToggleUpNextCommand.Execute(null);

        Chat.AskDeleteQueuedCommand.Execute(Chat.UpNext[1]);

        Assert.Equal(("Delete this queued input? It will not be sent.", "delete me"), (Chat.DeleteQuestion, Chat.DeletePreview));
        connection.Verify(x => x.DiscardSavedInput(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Chat.EditQueuedCommand.Execute(Chat.UpNext[0]);
        await Task.Delay(50);

        Assert.Equal(("edit me", (bool?)false, false), (Chat.Input, Chat.LoadedAutoCommit, Chat.IsUpNextOpen));
    }

    private WorkspaceInfo Made() => new() { Id = Guid.NewGuid(), Folder = "/data/sessions/new", Side = new SideSession { ParentId = workspace.Id, ParentName = "app", Name = "new", State = SideSessionState.Creating } };

    [Fact]
    public async Task RunInNewSession_TakesTheInputAndStartsASessionNamedAfterItFromTheCurrentFilesToBeBroughtIn()
    {
        WorkspaceViewModel view = await Started();
        SavedInput draft = Input("fix the login page today please now\nand test it");
        Saved(draft);
        connection.Setup(x => x.TakeSavedInput(workspace.Id, draft.Id, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        connection.Setup(x => x.CreateSideSession(workspace.Id, "fix the login page today", SideSessionStart.Files, draft.Text, true, It.IsAny<CancellationToken>())).ReturnsAsync(Made());
        WorkspaceInfo? made = null;
        view.SessionMade += session => made = session;

        view.RunInNewSessionCommand.Execute(draft);
        await Task.Delay(50);

        Assert.NotNull(made);
        connection.Verify(x => x.SaveInput(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        connection.Verify(x => x.QueueInput(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunInNewSession_ThatFailsPutsTheInputBackWhereItWasAndSaysWhy(bool queued)
    {
        WorkspaceViewModel view = await Started();
        SavedInput input = Input("do it", queued) with { AutoCommit = false };
        Saved(input);
        Told(queued ? Pending(Item(AgendaKind.Queued, "do it", input.Id, false)) : new Agenda());
        connection.Setup(x => x.TakeSavedInput(workspace.Id, input.Id, It.IsAny<CancellationToken>())).ReturnsAsync(input with { IsQueued = false });
        connection.Setup(x => x.CreateSideSession(workspace.Id, It.IsAny<string>(), It.IsAny<SideSessionStart>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("not a repository"));

        view.RunInNewSessionCommand.Execute(queued ? Chat.UpNext[0] : input);
        await Task.Delay(50);

        connection.Verify(x => x.QueueInput(workspace.Id, "do it", false, It.IsAny<CancellationToken>()), queued ? Times.Once() : Times.Never());
        connection.Verify(x => x.SaveInput(workspace.Id, "do it", false, It.IsAny<CancellationToken>()), queued ? Times.Never() : Times.Once());
        Assert.Equal(("not a repository", string.Empty), (Chat.Error, Chat.Input));
    }

    [Fact]
    public async Task RunInNewSession_ThatCanNotEvenPutTheInputBackKeepsItInTheMessageBox()
    {
        WorkspaceViewModel view = await Started();
        SavedInput draft = Input("do it");
        Saved(draft);
        connection.Setup(x => x.TakeSavedInput(workspace.Id, draft.Id, It.IsAny<CancellationToken>())).ReturnsAsync(draft);
        connection.Setup(x => x.CreateSideSession(workspace.Id, It.IsAny<string>(), It.IsAny<SideSessionStart>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));
        connection.Setup(x => x.SaveInput(workspace.Id, "do it", true, It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("down"));

        view.RunInNewSessionCommand.Execute(draft);
        await Task.Delay(50);

        Assert.Equal(("do it", "down"), (Chat.Input, Chat.Error));
    }

    [Theory]
    [InlineData("Fix the bug", "Fix the bug")]
    [InlineData("one two three four five six seven", "one two three four five")]
    [InlineData("\n  spaced\tout   words \nsecond line", "spaced out words")]
    [InlineData("Attached file: /data/a.txt", "Session")]
    [InlineData("main", "main 2")]
    [InlineData("taken", "taken 3")]
    [InlineData("supercalifragilisticexpialidocious_and_even_longer_than_that", "supercalifragilisticexpialidocious_and_e")]
    public void SessionNames_AreTheFirstWordsAndOfTheirOwn(string text, string expected) => Assert.Equal(expected, text.ToSessionName(["Taken", "taken 2"]));

    [Fact]
    public async Task LoopNext_SaysTheLoopIsPausedWhileTheWorkIsStopped()
    {
        WorkspaceViewModel view = await Started();
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", AutoCommit = true }, StartedAt = DateTimeOffset.UtcNow } });
        Assert.Contains("committing each run", view.LoopTip);

        Told(new Agenda { IsStopped = true, Items = [Item(AgendaKind.Loop, "Loop: x")] });

        Assert.Equal("The loop is paused since the work was stopped. It goes on after Resume.", view.LoopNext);
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
        Assert.True(view.ShowSendButton);
        Assert.True(view.SubmitCommand.CanExecute(null));
        Assert.True(Chat.QueueCommand.CanExecute(null));
        Assert.False(Chat.SendCommand.CanExecute(null));

        AutoCommit(AutoCommitState.Off);

        Assert.Equal(WorkspaceState.Ready, view.State);
    }

    [Fact]
    public async Task Queue_TakesWhatIsTypedWithItsAttachmentsOutOfTheBox()
    {
        WorkspaceViewModel view = await Started();
        Assert.False(view.ShowSendButton);
        connection.Setup(x => x.UploadAttachment(workspace.Id, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync("/data/0123abcd-a.txt");
        connection.Setup(x => x.QueueInput(workspace.Id, It.IsAny<string>(), true, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        Chat.Input = "  do it  ";
        Chat.Attach("a.txt", new byte[] { 1 });
        Assert.True(view.ShowSendButton);

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
    public async Task Queue_GivesBackWhatCouldNotBeQueued()
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
        Assert.Equal("Delete this draft? This cannot be undone.", Chat.DeleteQuestion);
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
    public async Task Notification_WaitsWhileTheAgendaHoldsMoreWork()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Told(Pending(Item(AgendaKind.Queued, "next", Guid.NewGuid(), true)));

        Working(false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.True(view.NotifyWhenReady);

        // The queued input starts, and only then leaves the agenda.
        Working(true);
        Told(new Agenda());
        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Working(false);

        notifier.Verify(x => x.Notify(workspace.Id, "app"), Times.Once);
        keeper.Verify(x => x.Release(workspace.Id), Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Notification_IsGivenWhenWhatWasUpNextIsStoppedOrRemovedWhileTheWorkspaceIsReady(bool stopped)
    {
        await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Agenda pending = Pending(Item(AgendaKind.Queued, "next", Guid.NewGuid(), true));
        Told(pending);
        Working(false);

        Told(stopped ? pending with { IsStopped = true } : new Agenda());

        notifier.Verify(x => x.Notify(workspace.Id, "app"), Times.Once);
    }

    [Fact]
    public async Task Notification_ReadsTheAgendaAgainAsWorkEndsBeforeTheServerHasToldOfWhatFollows()
    {
        WorkspaceViewModel view = await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        connection.Setup(x => x.GetAgenda(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Pending(Item(AgendaKind.Retry, "again")));

        Working(false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.True(view.NotifyWhenReady);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Notification_WaitsForALoopUnlessItIsStopped(bool stopped, bool notified)
    {
        await Started();
        Working(true);
        model.ToggleNotifyCommand.Execute(null);
        Raise(new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = workspace.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x" }, StartedAt = DateTimeOffset.UnixEpoch } });
        Told(new Agenda { IsStopped = stopped, Items = [Item(AgendaKind.Loop, "Loop: x")] });

        Working(false);

        notifier.Verify(x => x.Notify(workspace.Id, "app"), notified ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task NotifyButton_ShowsWhileMoreIsUpNext()
    {
        await Started();
        Assert.False(model.CanNotify);

        Told(Pending(Item(AgendaKind.BringIn, "Bring in fix", Guid.NewGuid(), true)));

        Assert.True(model.CanNotify);
    }

    private ActivityEntry Entry(bool attention, bool ended) => new() { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, EndedAt = ended ? DateTimeOffset.UtcNow : null, Source = ActivitySource.Queue, Title = "t", NeedsAttention = attention };

    private void Activity(ActivityEntry entry) => Raise(new RemoteEvent { Kind = RemoteEventKind.ActivityChanged, WorkspaceId = workspace.Id, ActivityEntry = entry });

    [Fact]
    public async Task EndedActivityThatNeedsAttention_IsAnnouncedOncePerEntry()
    {
        await Started();
        ActivityEntry entry = Entry(true, true);

        Activity(entry);
        Activity(entry with { Notes = ["more"] });
        Activity(Entry(true, false));
        Activity(Entry(false, true));

        notifier.Verify(x => x.Announce(workspace.Id, "app needs attention"), Times.Once);
        notifier.Verify(x => x.Announce(It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
    }
}
