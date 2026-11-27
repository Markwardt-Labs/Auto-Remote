namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SideSessionViewModelTests : IDisposable
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<IReadyNotifier> notifier = new();

    private readonly Mock<IWorkKeeper> keeper = new();

    private readonly WorkspaceInfo main = new() { Id = Guid.NewGuid(), Folder = "/work/app", LastUpdated = DateTimeOffset.UtcNow.AddHours(-1) };

    private readonly WorkspaceInfo other = new() { Id = Guid.NewGuid(), Folder = "/work/other", LastUpdated = DateTimeOffset.UtcNow };

    private readonly WorkspaceInfo side;

    private readonly HomeViewModel model;

    private WorkspaceCatalog catalog;

    public SideSessionViewModelTests()
    {
        side = Side("fix-login");
        catalog = Catalog(side);
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(() => catalog);
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetGitStatus(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetGitChanges(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([new GitChange { Path = "pending.txt", Status = GitChangeStatus.Added }]);
        model = new HomeViewModel(connection.Object, notifier: notifier.Object, keeper: keeper.Object);
    }

    public void Dispose() => model.Dispose();

    private WorkspaceInfo Side(string name, SideSessionState state = SideSessionState.Ready) => new() { Id = Guid.NewGuid(), Folder = $"/data/sessions/{name}", Side = new SideSession { ParentId = main.Id, ParentName = "app", Name = name, State = state } };

    private WorkspaceCatalog Catalog(params WorkspaceInfo[] sessions) => new() { Groups = [], Workspaces = [main, other], Sessions = sessions };

    private void Change(WorkspaceCatalog next)
    {
        catalog = next;
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkspacesChanged, WorkspaceId = Guid.Empty });
    }

    private WorkspaceInfo With(WorkspaceInfo session, Func<SideSession, SideSession> change) => session with { Side = change(session.Side!) };

    private void State(Guid id, WorkspaceState state) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = id, State = state });

    private void Working(Guid id, bool working) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = id, Working = working });

    private async Task Open() => await model.Start(main.Id);

    private SessionEntry Row(string name) => model.Sessions.Entries.Single(entry => entry.Name == name);

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task TheListHasMainFirstAndChoosingASessionShowsItUnderItsOwnName()
    {
        await Open();

        Assert.Equal(["Main", "fix-login"], model.Sessions.Entries.Select(entry => entry.Name));
        Assert.Equal([true, false], model.Sessions.Entries.Select(entry => entry.IsShown));
        Assert.False(Row("Main").HasActions);
        Assert.Equal(("app", false, true), (model.Title, model.IsSideSession, model.CanEditSelected));

        model.Sessions.IsOpen = true;
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));

        Assert.Equal(("app / fix-login", true, false, false), (model.Title, model.IsSideSession, model.CanEditSelected, model.Sessions.IsOpen));
        Assert.Equal(side.Id, model.Active!.Workspace.Id);
        Assert.Equal(main.Id, model.Selected!.Id);
        Assert.Equal(main.Id, model.SelectedEntry!.Workspace!.Id);
        Assert.True(Row("fix-login").IsShown);
        Assert.False(model.Active.Git.CanChangeRefs);
        Assert.False(model.Active.Settings.HasSessionSettings);

        model.Selected = other;
        Assert.Equal(["Main"], model.Sessions.Entries.Select(entry => entry.Name));
        model.Selected = model.Entries.Select(entry => entry.Workspace).First(workspace => workspace?.Id == main.Id);

        Assert.Equal(side.Id, model.Active.Workspace.Id);

        model.Sessions.ChooseCommand.Execute(Row("Main"));

        Assert.Equal((main.Id, "app", true), (model.Active.Workspace.Id, model.Title, model.Active.Settings.HasSessionSettings));
    }

    [Fact]
    public async Task AWorkspaceIsMarkedAndSortedAsWorkingWhenAnyOfItsSessionsWorks()
    {
        await Open();
        Assert.Equal([("other", false), ("app", false)], model.Entries.Select(entry => (entry.Workspace!.Name, entry.IsWorking)));

        State(side.Id, WorkspaceState.Working);

        Assert.Equal([("app", true), ("other", false)], model.Entries.Select(entry => (entry.Workspace!.Name, entry.IsWorking)));
        Assert.Equal(("Working", true), (Row("fix-login").Status, Row("fix-login").IsWorking));
        Assert.Equal("Ready", Row("Main").Status);

        State(side.Id, WorkspaceState.Ready);

        Assert.All(model.Entries, entry => Assert.False(entry.IsWorking));
    }

    [Fact]
    public async Task ASessionBeingSetUpOrIntegratedCannotBeOpenedAndAShownOneThatIsIntegratedGivesWayToMain()
    {
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        WorkspaceInfo setting = Side("new", SideSessionState.Creating);

        Change(Catalog(With(side, current => current with { State = SideSessionState.Integrating }), setting));

        Assert.Equal((main.Id, "app"), (model.Active!.Workspace.Id, model.Title));
        Assert.Equal(("Integrating", false, true, false), (Row("fix-login").Status, Row("fix-login").CanOpen, Row("fix-login").IsIntegrating, Row("fix-login").HasActions));
        Assert.Equal(("Setting Up", false, true, false), (Row("new").Status, Row("new").CanOpen, Row("new").CanDelete, Row("new").CanManage));

        model.Sessions.ChooseCommand.Execute(Row("fix-login"));

        Assert.Equal(main.Id, model.Active.Workspace.Id);

        Change(Catalog(side));

        Assert.Equal(main.Id, model.Active.Workspace.Id);
        Assert.True(Row("fix-login").CanOpen);
    }

    [Fact]
    public async Task ARemovedSessionGivesWayToMainAndItsKeepAliveIsReleased()
    {
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));

        Change(Catalog());

        Assert.Equal((main.Id, "app", false), (model.Active!.Workspace.Id, model.Title, model.IsSideSession));
        Assert.Equal(["Main"], model.Sessions.Entries.Select(entry => entry.Name));
        keeper.Verify(x => x.Release(side.Id), Times.Once);
    }

    [Fact]
    public async Task ANotificationOfASideSessionOpensItAndItsNameIsTheCurrentOne()
    {
        await model.Start(other.Id);

        await model.Show(side.Id);

        Assert.Equal((main.Id, side.Id, "app / fix-login"), (model.Selected!.Id, model.Active!.Workspace.Id, model.Title));

        Change(Catalog(With(side, current => current with { Name = "renamed" })));

        Assert.Equal((side.Id, "app / renamed"), (model.Active.Workspace.Id, model.Title));
    }

    [Fact]
    public async Task StartingFromASideSessionShowsIt()
    {
        await model.Start(side.Id);

        Assert.Equal((main.Id, side.Id), (model.Selected!.Id, model.Active!.Workspace.Id));
    }

    [Fact]
    public async Task ANewSessionDefaultsByWhetherMainsAiWorksWhenAskedAndIsSwitchedToOnceItIsSetUp()
    {
        WorkspaceInfo made = Side("second", SideSessionState.Creating);
        connection.Setup(x => x.CreateSideSession(main.Id, "second", SideSessionStart.Files, "do it", It.IsAny<CancellationToken>())).ReturnsAsync(made);
        await Open();

        model.Sessions.NewCommand.Execute(null);

        Assert.Equal((SessionDialog.New, true, false, "New session", "Create Session", "Cancel"), (model.Sessions.Dialog, model.Sessions.UseCurrentFiles, model.Sessions.UseCurrentCommit, model.Sessions.DialogTitle, model.Sessions.ConfirmText, model.Sessions.DismissText));

        // What main does after the dialog opened leaves the choice alone.
        State(main.Id, WorkspaceState.Working);
        Assert.True(model.Sessions.UseCurrentFiles);
        model.Sessions.DismissCommand.Execute(null);
        model.Sessions.NewCommand.Execute(null);
        Assert.True(model.Sessions.UseCurrentCommit);
        model.Sessions.UseCurrentFiles = true;

        model.Sessions.ConfirmCommand.Execute(null);
        Assert.Equal("Enter a name.", model.Sessions.Error);
        model.Sessions.Name = " second ";
        model.Sessions.FirstTask = " do it ";
        catalog = Catalog(side, made);
        model.Sessions.ConfirmCommand.Execute(null);
        await Until(() => model.Sessions.Dialog == SessionDialog.None);

        Assert.Equal(main.Id, model.Active!.Workspace.Id);
        model.Sessions.IsOpen = true;

        Change(Catalog(side, With(made, current => current with { State = SideSessionState.Ready })));

        Assert.False(model.Sessions.IsOpen);

        Assert.Equal((made.Id, true), (model.Active.Workspace.Id, model.Active.NotifyWhenReady));
        keeper.Verify(x => x.Hold(made.Id, "app / second"), Times.Once);
    }

    [Fact]
    public async Task ANewSessionIsNotSwitchedToWhenTheUserWentElsewhereMeanwhile()
    {
        WorkspaceInfo made = Side("second", SideSessionState.Creating);
        connection.Setup(x => x.CreateSideSession(main.Id, "second", SideSessionStart.Files, null, It.IsAny<CancellationToken>())).ReturnsAsync(made);
        await Open();
        model.Sessions.NewCommand.Execute(null);
        model.Sessions.Name = "second";
        catalog = Catalog(side, made);
        model.Sessions.ConfirmCommand.Execute(null);
        Change(catalog);

        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        Change(Catalog(side, With(made, current => current with { State = SideSessionState.Ready })));

        Assert.Equal(side.Id, model.Active!.Workspace.Id);
    }

    [Fact]
    public async Task ANewSessionMadeWhileAnotherSideSessionIsShownIsSwitchedToAsWell()
    {
        WorkspaceInfo made = Side("second", SideSessionState.Creating);
        connection.Setup(x => x.CreateSideSession(main.Id, "second", SideSessionStart.Files, null, It.IsAny<CancellationToken>())).ReturnsAsync(made);
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        model.Sessions.NewCommand.Execute(null);
        model.Sessions.Name = "second";
        catalog = Catalog(side, made);
        model.Sessions.ConfirmCommand.Execute(null);

        Change(Catalog(side, With(made, current => current with { State = SideSessionState.Ready })));

        Assert.Equal(made.Id, model.Active!.Workspace.Id);
    }

    [Fact]
    public async Task ARefusedNewSessionKeepsTheDialogWithTheReason()
    {
        connection.Setup(x => x.CreateSideSession(main.Id, "x", It.IsAny<SideSessionStart>(), null, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Side sessions need the repository to have a commit."));
        await Open();
        model.Sessions.NewCommand.Execute(null);
        model.Sessions.Name = "x";

        model.Sessions.ConfirmCommand.Execute(null);
        await Until(() => model.Sessions.HasError);

        Assert.Equal((SessionDialog.New, "Side sessions need the repository to have a commit."), (model.Sessions.Dialog, model.Sessions.Error));
    }

    [Fact]
    public async Task RenamingDeletingAndIntegratingAskFirst()
    {
        await Open();

        model.Sessions.RenameCommand.Execute(Row("fix-login"));
        Assert.Equal((SessionDialog.Rename, "fix-login"), (model.Sessions.Dialog, model.Sessions.Name));
        model.Sessions.Name = "better";
        model.Sessions.ConfirmCommand.Execute(null);

        model.Sessions.DeleteCommand.Execute(Row("fix-login"));
        Assert.Equal(("Delete session?", "\"fix-login\" and all of its changes will be permanently discarded.", true), (model.Sessions.DialogTitle, model.Sessions.DialogText, model.Sessions.IsDestructive));
        model.Sessions.DismissCommand.Execute(null);
        connection.Verify(x => x.DeleteSideSession(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Sessions.DeleteCommand.Execute(Row("fix-login"));
        model.Sessions.ConfirmCommand.Execute(null);

        model.Sessions.IntegrateCommand.Execute(Row("fix-login"));
        Assert.Contains("cannot be undone", model.Sessions.DialogText);
        connection.Verify(x => x.IntegrateSideSession(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Sessions.ConfirmCommand.Execute(null);

        connection.Verify(x => x.RenameSideSession(side.Id, "better", It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.DeleteSideSession(side.Id, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.IntegrateSideSession(side.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TurningOnIntegrateWhenFinishedWarnsAndTurningItOffDoesNot()
    {
        await Open();

        model.Sessions.WhenFinishedCommand.Execute(Row("fix-login"));

        Assert.Equal((SessionDialog.IntegrateWhenFinished, "Turn On"), (model.Sessions.Dialog, model.Sessions.ConfirmText));
        Assert.Contains("This cannot be undone.", model.Sessions.DialogText);
        connection.Verify(x => x.SetIntegrateWhenFinished(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Sessions.ConfirmCommand.Execute(null);
        connection.Verify(x => x.SetIntegrateWhenFinished(side.Id, true, It.IsAny<CancellationToken>()), Times.Once);

        Change(Catalog(With(side, current => current with { IntegrateWhenFinished = true })));
        Assert.Equal(("✓ Integrate When Finished", "Ready - Integrates when finished"), (Row("fix-login").WhenFinishedText, Row("fix-login").Detail));
        model.Sessions.WhenFinishedCommand.Execute(Row("fix-login"));

        Assert.Equal(SessionDialog.None, model.Sessions.Dialog);
        connection.Verify(x => x.SetIntegrateWhenFinished(side.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CancellingAnIntegrationWarnsInTheseWordsAndTheQuestionGoesWhenItFinishesFirst()
    {
        catalog = Catalog(With(side, current => current with { State = SideSessionState.Integrating }));
        await Open();

        model.Sessions.CancelIntegrationCommand.Execute(Row("fix-login"));

        Assert.Equal(("Cancel Integration?", "Main may already contain some of this session's changes. They will not be undone, and you may need to clean up main yourself. The AI may make a few more edits before it stops.", "Cancel Integration", "Keep Integrating"),
            (model.Sessions.DialogTitle, model.Sessions.DialogText, model.Sessions.ConfirmText, model.Sessions.DismissText));
        model.Sessions.DismissCommand.Execute(null);
        connection.Verify(x => x.CancelIntegration(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        model.Sessions.CancelIntegrationCommand.Execute(Row("fix-login"));
        model.Sessions.ConfirmCommand.Execute(null);
        connection.Verify(x => x.CancelIntegration(side.Id, It.IsAny<CancellationToken>()), Times.Once);

        model.Sessions.CancelIntegrationCommand.Execute(Row("fix-login"));
        Change(Catalog());

        Assert.Equal(SessionDialog.None, model.Sessions.Dialog);
    }

    [Fact]
    public async Task ARowsActionsOpenOneAtATime()
    {
        WorkspaceInfo second = Side("second");
        catalog = Catalog(side, second);
        await Open();

        model.Sessions.ExpandCommand.Execute(Row("fix-login"));
        model.Sessions.ExpandCommand.Execute(Row("second"));

        Assert.Equal([false, false, true], model.Sessions.Entries.Select(entry => entry.IsExpanded));
        model.Sessions.ExpandCommand.Execute(Row("second"));
        Assert.All(model.Sessions.Entries, entry => Assert.False(entry.IsExpanded));
    }

    [Fact]
    public async Task ChangesSinceSessionStartListsThemInTheFileTreeAndTheirDiffsCompareWithTheStart()
    {
        connection.Setup(x => x.GetSessionChanges(side.Id, It.IsAny<CancellationToken>())).ReturnsAsync([new GitChange { Path = "since.txt", Status = GitChangeStatus.Added }]);
        connection.Setup(x => x.GetSessionDiff(side.Id, "since.txt", It.IsAny<CancellationToken>())).ReturnsAsync(new GitDiff { Path = "since.txt", Lines = [] });
        await Open();

        model.Sessions.ChangesCommand.Execute(Row("fix-login"));

        WorkspaceViewModel view = model.Active!;
        await Until(() => view.Git.ChangeTree.Select(node => node.Name).SequenceEqual(["since.txt"]));
        Assert.Equal((side.Id, true, true, false, WorkspaceTab.Files), (view.Workspace.Id, view.Git.IsSessionChangesMode, view.Git.IsChangesMode, view.Git.IsUncommittedMode, view.Tab));
        Assert.True(view.Files.ShowChangeTree);

        view.Git.ChangeTree[0].OpenCommand.Execute(null);
        await Until(() => view.Files.PreviewTitle?.Contains("since.txt") == true);
        connection.Verify(x => x.GetSessionDiff(side.Id, "since.txt", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        connection.Verify(x => x.GetGitDiff(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        view.Git.IsUncommittedMode = true;
        await Until(() => view.Git.ChangeTree.Select(node => node.Name).SequenceEqual(["pending.txt"]));
        Assert.False(view.Git.IsSessionChangesMode);

        view.Git.IsUncommittedMode = false;
        Assert.False(view.Git.IsChangesMode);
    }

    [Fact]
    public async Task AFirstTaskThatWasNotSentWaitsInTheMessageBoxUnderTheReason()
    {
        catalog = Catalog(With(side, current => current with { Error = "Setting up the session failed: no", UnsentTask = "do the thing" }));
        await model.Start(side.Id);
        WorkspaceViewModel view = model.Active!;

        await Until(() => view.Chat.Input == "do the thing");
        Assert.Equal((true, "Setting up the session failed: no"), (view.HasSideError, view.SideError));

        view.Chat.Input = string.Empty;
        Change(catalog);
        Assert.Equal(string.Empty, view.Chat.Input);

        Change(Catalog(side));
        Assert.False(view.HasSideError);
    }

    [Fact]
    public async Task ARemovedSessionsNotificationIsWithdrawnAndWorkThrownAwayEndsInNone()
    {
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        WorkspaceViewModel view = model.Active!;
        Working(side.Id, true);
        view.NotifyWhenReady = true;

        Change(Catalog(With(side, current => current with { State = SideSessionState.Deleting })));
        Working(side.Id, false);

        Assert.False(view.NotifyWhenReady);
        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Change(Catalog());
        notifier.Verify(x => x.Clear(side.Id), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeletingASessionFromHereDisarmsItBeforeTheServerEndsItsWork()
    {
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        WorkspaceViewModel view = model.Active!;
        view.NotifyWhenReady = true;
        bool armedAtDelete = true;
        connection.Setup(x => x.DeleteSideSession(side.Id, It.IsAny<CancellationToken>())).Callback(() => armedAtDelete = view.NotifyWhenReady).Returns(Task.CompletedTask);

        model.Sessions.DeleteCommand.Execute(Row("fix-login"));
        model.Sessions.ConfirmCommand.Execute(null);

        Assert.False(armedAtDelete);
    }

    [Fact]
    public async Task AFirstTaskIsFollowedHoweverFarTheUserGoesWhileTheSessionIsSetUp()
    {
        WorkspaceInfo made = Side("second", SideSessionState.Creating);
        connection.Setup(x => x.CreateSideSession(main.Id, "second", SideSessionStart.Files, "do it", It.IsAny<CancellationToken>())).ReturnsAsync(made);
        await Open();
        model.Sessions.NewCommand.Execute(null);
        model.Sessions.Name = "second";
        model.Sessions.FirstTask = "do it";
        catalog = Catalog(side, made);
        model.Sessions.ConfirmCommand.Execute(null);
        await Until(() => model.Sessions.Dialog == SessionDialog.None);
        keeper.Verify(x => x.Hold(made.Id, "app / second"), Times.Once);
        model.Selected = other;

        Change(Catalog(side, With(made, current => current with { State = SideSessionState.Ready })));
        Working(made.Id, true);
        Working(made.Id, false);

        Assert.Equal(other.Id, model.Active!.Workspace.Id);
        notifier.Verify(x => x.Notify(made.Id, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task ASlowRequestsFailureIsNotShownInTheNextDialogNorDoesItHoldItBack()
    {
        TaskCompletionSource<WorkspaceInfo> slow = new();
        connection.Setup(x => x.CreateSideSession(main.Id, "x", It.IsAny<SideSessionStart>(), null, It.IsAny<CancellationToken>())).Returns(slow.Task);
        string? failed = null;
        await Open();
        model.Sessions.Failed += message => failed = message;
        model.Sessions.NewCommand.Execute(null);
        model.Sessions.Name = "x";
        model.Sessions.ConfirmCommand.Execute(null);
        Assert.False(model.Sessions.CanConfirm);
        model.Sessions.DismissCommand.Execute(null);
        model.Sessions.DeleteCommand.Execute(Row("fix-login"));
        Assert.True(model.Sessions.CanConfirm);
        Assert.True(model.Sessions.ConfirmCommand.CanExecute(null));

        slow.SetException(new RemoteException("it did not work"));
        await Until(() => failed is not null);

        Assert.Equal((SessionDialog.Delete, null, "it did not work"), (model.Sessions.Dialog, model.Sessions.Error, failed));
    }

    [Fact]
    public async Task MainShownInThePlaceOfASessionStaysWhenThatSessionBecomesReadyAgain()
    {
        await Open();
        model.Sessions.ChooseCommand.Execute(Row("fix-login"));
        model.Selected = other;
        Change(Catalog(With(side, current => current with { State = SideSessionState.Integrating })));
        model.Selected = main;
        Assert.Equal(main.Id, model.Active!.Workspace.Id);

        Change(Catalog(side));

        Assert.Equal(main.Id, model.Active.Workspace.Id);
    }

    [Fact]
    public async Task ANotificationOfSomethingThatIsGoneLeavesTheScreenAsItIs()
    {
        await Open();

        await model.Show(Guid.NewGuid());

        Assert.Equal(main.Id, model.Selected!.Id);
        Assert.False(model.IsDrawerOpen);
    }

    [Fact]
    public async Task TasksASessionDelegatedShowUnderItsWorkspace()
    {
        SwarmTaskInfo task = new() { Id = Guid.NewGuid(), JobId = Guid.NewGuid(), WorkspaceId = side.Id, Title = "from the side", State = SwarmTaskState.Running, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        connection.Setup(x => x.GetSwarm(It.IsAny<CancellationToken>())).ReturnsAsync(new SwarmStatus { Server = new SwarmServer { Name = "s" }, Tasks = [task] });

        await Open();

        await Until(() => model.Entries.Any(entry => entry.IsTask));
        int at = model.Entries.IndexOf(model.Entries.Single(entry => entry.IsTask));
        Assert.Equal(main.Id, model.Entries[at - 1].Workspace!.Id);
    }

    [Fact]
    public async Task AFailureToListTheChangesSinceTheStartSaysSoInsteadOfShowingNone()
    {
        connection.Setup(x => x.GetSessionChanges(side.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("git could not read the files"));
        await Open();

        model.Sessions.ChangesCommand.Execute(Row("fix-login"));

        await Until(() => model.Active!.Git.SessionChangesError == "git could not read the files");
        Assert.True(model.Active!.Git.IsRepo);
        model.Active.Git.IsUncommittedMode = true;
        Assert.False(model.Active.Git.HasSessionChangesError);
    }

    [Fact]
    public void BranchesOfSideSessionsOfferNoActions()
    {
        GitViewModel git = new(connection.Object, main.Id);
        GitRefViewModel session = new(new GitRef { Name = "fix-login", Kind = GitRefKind.Session }, git, "main");
        GitRefViewModel branch = new(new GitRef { Name = "topic", Kind = GitRefKind.Branch }, git, "main");
        GitViewModel inside = new(connection.Object, side.Id) { IsSideSession = true };
        GitRefViewModel seen = new(new GitRef { Name = "topic", Kind = GitRefKind.Branch }, inside, "fix-login");

        Assert.Equal((true, false, false, false, false), (session.IsSession, session.CanCheckout, session.CanDelete, session.HasRelative, session.IsBranch));
        Assert.Equal((true, true, true), (branch.CanCheckout, branch.CanDelete, branch.HasRelative));
        Assert.Equal((false, false, false), (seen.CanCheckout, seen.CanDelete, seen.HasRelative));
        Assert.Equal((false, false, false, false), (inside.BranchCommand.CanExecute(null), inside.TagCommand.CanExecute(null), inside.RemoteCommand.CanExecute(null), inside.FetchCommand.CanExecute(null)));
        Assert.True(inside.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheSettingsOfNewSessionsAreSavedWithTheWorkspaces()
    {
        connection.Setup(x => x.GetWorkspaceSettings(main.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { SessionFiles = ".env" });
        WorkspaceSettings? saved = null;
        connection.Setup(x => x.SetWorkspaceSettings(main.Id, It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, WorkspaceSettings given, CancellationToken _) => saved = given);
        SettingsViewModel settings = new(connection.Object, main);
        await Until(() => settings.SessionFiles == ".env");

        settings.SessionSetupScript = " setup.cs ";
        settings.SessionFiles = "  ";
        Assert.True(await settings.Apply());

        Assert.Equal(("setup.cs", null), (saved!.SessionSetupScript, saved.SessionFiles));
    }
}
