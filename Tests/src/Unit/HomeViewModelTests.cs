namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class HomeViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/a" };

    private readonly WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/b" };

    private readonly HomeViewModel model;

    public HomeViewModelTests()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first, second));
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        model = new HomeViewModel(connection.Object);
    }

    private WorkspaceCatalog Catalog(WorkspaceGroup[] groups, params WorkspaceInfo[] workspaces) => new() { Groups = groups, Workspaces = workspaces };

    [Fact]
    public async Task SwitchingToAnotherWorkspace_AppliesTheSettingsEditsOfTheOneLeft()
    {
        connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.SetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, WorkspaceSettings given, CancellationToken _) => given);
        await model.Start();
        model.Active!.Settings.IsShellAllowed = true;

        model.Selected = second;

        await Task.Delay(300);
        connection.Verify(x => x.SetWorkspaceSettings(first.Id, It.Is<WorkspaceSettings>(sent => sent.IsShellAllowed), It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.SetWorkspaceSettings(second.Id, It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MovingTheWorkspaceToAFolder_AppliesTheSettingsEditsFirst()
    {
        connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.SetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, WorkspaceSettings given, CancellationToken _) => given);
        await model.Start();
        model.Active!.Settings.IsShellAllowed = true;
        WorkspaceInfo? requested = null;
        model.MoveWorkspaceRequested += workspace => requested = workspace;

        model.MoveWorkspaceToFolderCommand.Execute(null);
        for (int attempt = 0; attempt < 500 && requested is null; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(first, requested);
        connection.Verify(x => x.SetWorkspaceSettings(first.Id, It.Is<WorkspaceSettings>(sent => sent.IsShellAllowed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MovingTheWorkspaceToAFolder_WaitsForTheEditsToBeAcceptedAndDoesNotMoveWhenTheyAreNot()
    {
        connection.Setup(x => x.GetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings());
        connection.Setup(x => x.SetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        await model.Start();
        model.Active!.Settings.IsShellAllowed = true;
        bool requested = false;
        model.MoveWorkspaceRequested += _ => requested = true;

        model.MoveWorkspaceToFolderCommand.Execute(null);
        await Task.Delay(300);

        Assert.False(requested);
        Assert.Equal("refused", model.Active.Settings.Error);
    }

    [Fact]
    public async Task Start_ListsWorkspacesAndFocusesFirst()
    {
        await model.Start();

        Assert.Equal([first, second], model.Entries.Select(entry => entry.Workspace));
        Assert.Equal(first, model.Selected);
        Assert.Equal("a", model.Title);
        Assert.NotNull(model.Active);
        Assert.False(model.IsDrawerOpen);
        Assert.Equal(first, model.SelectedEntry?.Workspace);
    }

    [Fact]
    public async Task Filter_KeepsMatchingWorkspacesWithTheirGroupsAndOpensCollapsedGroups()
    {
        WorkspaceGroup clients = new() { Id = Guid.NewGuid(), Name = "Clients" };
        WorkspaceGroup empty = new() { Id = Guid.NewGuid(), Name = "Empty" };
        WorkspaceInfo acme = new() { Id = Guid.NewGuid(), Folder = "/work/acme", GroupId = clients.Id };
        WorkspaceInfo other = new() { Id = Guid.NewGuid(), Folder = "/work/other", GroupId = clients.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([clients, empty], acme, other, first));
        await model.Start();

        model.Filter = "ACM";

        Assert.Equal([(object)clients, acme], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.False(model.HasNoMatches);

        model.Filter = "client";

        Assert.Equal([(object)clients, acme, other], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

        model.Filter = "/work/a";

        Assert.Equal([(object)clients, acme, first], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

        model.Filter = "nothing";

        Assert.Empty(model.Entries);
        Assert.True(model.HasNoMatches);

        model.Filter = " ";

        Assert.Equal([(object)clients, empty, first], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.True(model.Entries[0].IsCollapsed);
    }

    private void Working(WorkspaceInfo workspace, bool working) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working });

    private async Task<(HomeViewModel Model, Mock<IReadyNotifier> Notifier)> Notifying()
    {
        Mock<IReadyNotifier> notifier = new();
        HomeViewModel notifying = new(connection.Object, ClientLayout.Tabs, null, null, null, notifier.Object);
        await notifying.Start();
        return (notifying, notifier);
    }

    [Fact]
    public async Task Notifier_SaysTheWorkspaceIsReadyOnceWhenWorkThisClientGaveEnds()
    {
        (HomeViewModel notifying, Mock<IReadyNotifier> notifier) = await Notifying();
        notifying.Active!.Chat.Input = "do it";

        notifying.Active.Chat.SendCommand.Execute(null);
        await Task.Delay(50);
        Working(first, true);
        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Working(first, false);

        notifier.Verify(x => x.Notify(first.Id, "a"), Times.Once);

        Working(first, true);
        Working(first, false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Keeper_HoldsTheAppFromSubmittingAPromptUntilTheWorkspaceIsReady()
    {
        Mock<IWorkKeeper> keeper = new();
        HomeViewModel keeping = new(connection.Object, ClientLayout.Tabs, null, null, null, null, keeper.Object);
        await keeping.Start();
        keeping.Active!.Chat.Input = "do it";

        keeping.Active.Chat.SendCommand.Execute(null);
        await Task.Delay(50);
        keeper.Verify(x => x.Hold(first.Id, "a"), Times.Once);
        keeper.Verify(x => x.Release(It.IsAny<Guid>()), Times.Never);

        Working(first, true);
        keeper.Verify(x => x.Release(It.IsAny<Guid>()), Times.Never);
        Working(first, false);

        keeper.Verify(x => x.Release(first.Id), Times.Once);
    }

    [Fact]
    public async Task Keeper_LetsGoWhenThePromptCouldNotBeSentAndIgnoresWorkAnotherClientGave()
    {
        Mock<IWorkKeeper> keeper = new();
        HomeViewModel keeping = new(connection.Object, ClientLayout.Tabs, null, null, null, null, keeper.Object);
        await keeping.Start();
        Working(first, true);
        Working(first, false);
        keeper.Verify(x => x.Hold(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        connection.Setup(x => x.Prompt(first.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        keeping.Active!.Chat.Input = "do it";

        keeping.Active.Chat.SendCommand.Execute(null);
        await Task.Delay(50);

        keeper.Verify(x => x.Hold(first.Id, "a"), Times.Once);
        keeper.Verify(x => x.Release(first.Id), Times.Once);
    }

    [Fact]
    public async Task Notifier_StaysQuietWhileALoopRunsAndForgetsTheWorkItWasWaitingFor()
    {
        connection.Setup(x => x.GetLoop(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new LoopInfo { Settings = new LoopSettings { Prompt = "check" }, StartedAt = DateTimeOffset.UnixEpoch });
        (HomeViewModel notifying, Mock<IReadyNotifier> notifier) = await Notifying();
        await Task.Delay(100);
        notifying.Active!.Chat.Input = "do it";

        notifying.Active.Chat.SendCommand.Execute(null);
        await Task.Delay(50);
        Working(first, true);
        Working(first, false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
        Assert.False(notifying.Active.NotifyWhenReady);
    }

    [Fact]
    public async Task Notifier_StaysQuietForWorkAnotherClientGave()
    {
        (_, Mock<IReadyNotifier> notifier) = await Notifying();

        Working(first, true);
        Working(first, false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Notifier_StaysQuietWhenThePromptCouldNotBeSent()
    {
        (HomeViewModel notifying, Mock<IReadyNotifier> notifier) = await Notifying();
        connection.Setup(x => x.Prompt(first.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("refused"));
        notifying.Active!.Chat.Input = "do it";

        notifying.Active.Chat.SendCommand.Execute(null);
        await Task.Delay(50);
        Working(first, true);
        Working(first, false);

        notifier.Verify(x => x.Notify(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task StopAndCancel_AskFirstThenAskTheServerAndCloseWhenTheWorkEndsMeanwhile()
    {
        await model.Start();
        Working(first, true);
        Assert.True(model.BeginStopCommand.CanExecute(null));

        model.BeginStopCommand.Execute(null);
        Assert.True(model.IsStoppingWork);
        Assert.Equal("Keep Working", model.CancelEditText);
        connection.Verify(x => x.StopWorkspace(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        model.ConfirmHaltCommand.Execute(null);
        await Task.Delay(50);
        Assert.False(model.IsEditing);
        connection.Verify(x => x.StopWorkspace(first.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StopQuestion_ClosesWhenTheWorkEndsWhileItIsShowing()
    {
        await model.Start();
        Working(first, true);
        model.BeginStopCommand.Execute(null);
        Assert.True(model.IsStoppingWork);

        Working(first, false);

        Assert.False(model.IsEditing);
        Assert.False(model.BeginStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Show_FocusesTheWorkspaceAndClosesTheDrawerAndDialogs()
    {
        await model.Start();
        model.IsDrawerOpen = true;
        model.BeginRenameCommand.Execute(null);

        await model.Show(second.Id);

        Assert.Equal(second, model.Selected);
        Assert.False(model.IsDrawerOpen);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task Show_ReadsTheWorkspacesAgainForOneItDoesNotKnowYet()
    {
        await model.Start();
        WorkspaceInfo added = new() { Id = Guid.NewGuid(), Folder = "/work/new" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first, second, added));

        await model.Show(added.Id);

        Assert.Equal(added, model.Selected);
    }

    private async Task Changed(WorkspaceCatalog catalog)
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(catalog);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkspacesChanged, WorkspaceId = Guid.Empty });

        await Task.Delay(120);
    }

    [Fact]
    public async Task WorkspacesChanged_ShowsNewGroupsAndWorkspacesWithoutDisturbingTheOneOpen()
    {
        await model.Start();
        WorkspaceViewModel? open = model.Active;
        model.IsDrawerOpen = true;
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo added = new() { Id = Guid.NewGuid(), Folder = "/work/new" };

        await Changed(Catalog([group], first with { GroupId = group.Id }, second, added));

        Assert.Equal([(object)group, second, added], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.Same(open, model.Active);
        Assert.Equal(first.Id, model.Selected?.Id);
        Assert.True(model.IsDrawerOpen);
    }

    [Fact]
    public async Task WorkspacesChanged_ClearsTheViewWhenTheOpenWorkspaceWasDeletedElsewhere()
    {
        await model.Start();
        Assert.Equal(first, model.Selected);

        await Changed(Catalog([], second));

        Assert.Null(model.Selected);
        Assert.Null(model.Active);
        Assert.True(model.IsDrawerOpen);
        Assert.Equal([second], model.Entries.Select(entry => entry.Workspace));
    }

    [Fact]
    public async Task WorkspacesChanged_ReopensTheOpenWorkspaceWhenItWasRenamedOrReconfiguredElsewhere()
    {
        await model.Start();
        WorkspaceViewModel? old = model.Active;

        await Changed(Catalog([], first with { Folder = "/work/renamed" }, second));

        Assert.Equal("renamed", model.Title);
        Assert.NotSame(old, model.Active);
        Assert.Equal("/work/renamed", model.Active?.Workspace.Folder);

        WorkspaceViewModel? renamed = model.Active;
        await Changed(Catalog([], first with { Folder = "/work/renamed", Service = AiService.Codex }, second));

        Assert.NotSame(renamed, model.Active);
        Assert.Equal(AiService.Codex, model.Active?.Workspace.Service);
    }

    [Fact]
    public async Task WorkspacesChanged_ForgetsTheViewsOfWorkspacesThatWereDeleted()
    {
        await model.Start();
        model.Selected = second;
        model.Selected = first;

        await Changed(Catalog([], first));
        model.Selected = null;
        await Changed(Catalog([], first, second));
        model.Selected = second;

        Assert.Equal(second, model.Selected);
        Assert.Equal(second.Id, model.Active?.Workspace.Id);
    }

    [Fact]
    public async Task Reconnecting_ReadsTheWorkspacesAgainBecauseEventsWereMissed()
    {
        await model.Start();
        WorkspaceInfo added = new() { Id = Guid.NewGuid(), Folder = "/work/new" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first, second, added));

        connection.Raise(x => x.Reconnected += null);
        await Task.Delay(150);

        Assert.Contains(model.Entries, entry => entry.Workspace == added);
    }

    [Fact]
    public async Task WorkspacesChanged_UpdatesTheListInPlaceSoItsScrollPositionAndSelectionAreNotDisturbed()
    {
        WorkspaceInfo third = new() { Id = Guid.NewGuid(), Folder = "/work/c" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first, second, third));
        await model.Start();
        model.Selected = second;
        DrawerEntry[] before = [.. model.Entries];
        DrawerEntry? selectedBefore = model.SelectedEntry;
        List<string> collectionChanges = [];
        List<string> propertyChanges = [];
        model.Entries.CollectionChanged += (_, args) => collectionChanges.Add(args.Action.ToString());
        model.PropertyChanged += (_, args) => propertyChanges.Add(args.PropertyName ?? string.Empty);

        await Changed(Catalog([], first, second, third));

        Assert.Empty(collectionChanges);
        Assert.DoesNotContain(nameof(HomeViewModel.SelectedEntry), propertyChanges);
        Assert.Equal(before, model.Entries.ToArray());
        Assert.Same(selectedBefore, model.SelectedEntry);

        WorkspaceInfo added = new() { Id = Guid.NewGuid(), Folder = "/work/d" };
        collectionChanges.Clear();
        await Changed(Catalog([], first, second, third, added));

        Assert.Equal(["Add"], collectionChanges);
        Assert.Same(before[0], model.Entries[0]);
        Assert.Same(before[2], model.Entries[2]);
        Assert.Same(selectedBefore, model.SelectedEntry);

        Assert.Same(selectedBefore, model.SelectedEntry);
    }

    [Fact]
    public async Task NewGroup_AsksWhereToCreateItAndCreatesItThereAndOpensThatGroup()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, inner], first, second));
        connection.Setup(x => x.CreateGroup(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceGroup { Id = Guid.NewGuid(), Name = "New" });
        await model.Start();

        model.BeginNewGroupCommand.Execute(null);

        Assert.True(model.IsCreatingGroup);
        Assert.Equal(["Top level", "Work", "Work / Inner"], model.NewGroupParent.Choices.Select(choice => choice.Name));
        Assert.Null(model.NewGroupParent.GroupId);

        model.NewGroupParent.Selected = model.NewGroupParent.Choices[1];
        model.RenameText = "New";
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.CreateGroup("New", work.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.Entries.First(entry => entry.Group == work).IsCollapsed);
    }

    [Fact]
    public async Task NewGroup_AtTheTopLevelIsTheDefault()
    {
        connection.Setup(x => x.CreateGroup(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceGroup { Id = Guid.NewGuid(), Name = "Top" });
        await model.Start();

        model.BeginNewGroupCommand.Execute(null);
        model.RenameText = "Top";
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.CreateGroup("Top", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FocusingAWorkspaceInACollapsedGroup_OpensTheGroupsAroundItSoItShows()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id };
        WorkspaceInfo deep = first with { GroupId = inner.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, inner], deep, second));
        await model.Start();
        Assert.Equal([(object)work, second], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

        await model.Show(deep.Id);

        Assert.Equal([(object)work, inner, deep, second], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.Equal(deep, model.SelectedEntry?.Workspace);
    }

    [Fact]
    public async Task Entries_OrderWorkspacesBySessionLastUpdateLatestFirstAndGroupsStayBeforeThem()
    {
        DateTimeOffset now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        WorkspaceInfo oldest = new() { Id = Guid.NewGuid(), Folder = "/work/zzz", LastUpdated = now.AddDays(-2) };
        WorkspaceInfo newest = new() { Id = Guid.NewGuid(), Folder = "/work/aaa", LastUpdated = now };
        WorkspaceInfo middle = new() { Id = Guid.NewGuid(), Folder = "/work/mmm", LastUpdated = now.AddDays(-1) };
        WorkspaceInfo unknown = new() { Id = Guid.NewGuid(), Folder = "/work/bbb" };
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup sub = new() { Id = Guid.NewGuid(), Name = "Sub", ParentId = work.Id };
        WorkspaceInfo inWork = new() { Id = Guid.NewGuid(), Folder = "/work/in", GroupId = work.Id, LastUpdated = now.AddDays(-3) };
        WorkspaceInfo inWorkNewer = new() { Id = Guid.NewGuid(), Folder = "/work/out", GroupId = work.Id, LastUpdated = now.AddDays(-1) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, sub], oldest, inWork, newest, middle, unknown, inWorkNewer));

        await model.Start();
        model.SelectedEntry = model.Entries[0];

        Assert.Equal([(object)work, sub, inWorkNewer, inWork, newest, middle, oldest, unknown], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
    }

    [Fact]
    public async Task Title_SaysWhenTheConnectionIsBeingReopened()
    {
        await model.Start();
        Assert.Equal("a", model.Title);

        connection.Raise(x => x.Lost += null);
        Assert.True(model.IsOffline);
        Assert.Equal("a (reconnecting…)", model.Title);

        connection.Raise(x => x.Reconnected += null);
        Assert.False(model.IsOffline);
        Assert.Equal("a", model.Title);
    }

    [Fact]
    public async Task Filter_IgnoresTapsOnGroupsSoTheirOpenStateIsKeptForWhenItIsCleared()
    {
        WorkspaceGroup clients = new() { Id = Guid.NewGuid(), Name = "Clients" };
        WorkspaceInfo acme = new() { Id = Guid.NewGuid(), Folder = "/work/acme", GroupId = clients.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([clients], acme, first));
        await model.Start();
        model.Filter = "acme";

        model.SelectedEntry = model.Entries.First(entry => entry.Group == clients);
        model.Filter = string.Empty;

        Assert.True(model.Entries.First(entry => entry.Group == clients).IsCollapsed);
        Assert.DoesNotContain(model.Entries, entry => entry.Workspace == acme);
    }

    [Fact]
    public async Task ShiftCommands_AskTheServerToMoveAGroupAndShowTheNewOrder()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup other = new() { Id = Guid.NewGuid(), Name = "Play" };
        await model.Start();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([other, group], second, first));

        model.ShiftGroupUpCommand.Execute(group);
        await Task.Delay(50);

        connection.Verify(x => x.ShiftGroup(group.Id, -1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([other, group, first, second], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

        model.ShiftGroupDownCommand.Execute(group);

        connection.Verify(x => x.ShiftGroup(group.Id, 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_WithoutFocusingFirst_StaysBlankWithTheDrawerOpen()
    {
        await model.Start(null, false);

        Assert.Equal([first, second], model.Entries.Select(entry => entry.Workspace));
        Assert.Null(model.Selected);
        Assert.Null(model.Active);
        Assert.True(model.IsDrawerOpen);
    }

    [Fact]
    public async Task Start_FocusesRequestedWorkspace()
    {
        await model.Start(second.Id);

        Assert.Equal(second, model.Selected);
    }

    [Fact]
    public async Task Start_WithNoWorkspaces_OpensDrawer()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([]));

        await model.Start();

        Assert.Null(model.Selected);
        Assert.True(model.IsDrawerOpen);
        Assert.False(model.HasSelection);
    }

    [Fact]
    public async Task Start_ShowsConnectionFailure()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("lost"));

        await model.Start();

        Assert.Equal("lost", model.Error);
    }

    [Fact]
    public async Task Selecting_SwitchesWorkspaceAndClosesDrawer()
    {
        await model.Start();
        model.IsDrawerOpen = true;

        model.Selected = second;

        Assert.Equal(second, model.Active?.Workspace);
        Assert.False(model.IsDrawerOpen);
    }

    [Fact]
    public async Task TappingAWorkspaceEntry_FocusesIt()
    {
        await model.Start();

        model.SelectedEntry = model.Entries.First(entry => entry.Workspace == second);

        Assert.Equal(second, model.Selected);
    }

    [Fact]
    public async Task Entries_ListGroupsFirstWithTheirWorkspacesThenUngrouped()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo grouped = first with { GroupId = work.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], grouped, second));

        await model.Start();
        Assert.Equal([(object)work, second], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        model.SelectedEntry = model.Entries[0];

        Assert.Equal(work, model.Entries[0].Group);
        Assert.Equal(grouped, model.Entries[1].Workspace);
        Assert.Equal(1, model.Entries[1].Depth);
        Assert.Equal(second, model.Entries[2].Workspace);
        Assert.Equal(0, model.Entries[2].Depth);
        Assert.Equal(second, model.Selected);
    }

    [Fact]
    public async Task TappingAGroupEntry_CollapsesAndExpandsIt()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first with { GroupId = work.Id }, second));
        await model.Start();

        Assert.Equal(2, model.Entries.Count);
        Assert.True(model.Entries[0].IsCollapsed);
        Assert.StartsWith("▸", model.Entries[0].Text);

        model.SelectedEntry = model.Entries[0];
        Assert.Equal(3, model.Entries.Count);
        Assert.StartsWith("▾", model.Entries[0].Text);

        model.SelectedEntry = model.Entries[0];
        Assert.Equal(2, model.Entries.Count);
    }

    [Fact]
    public async Task Entries_NestGroupsRecursivelyWithSubgroupsBeforeWorkspaces()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id };
        WorkspaceGroup deepest = new() { Id = Guid.NewGuid(), Name = "Deepest", ParentId = inner.Id };
        WorkspaceInfo top = first with { GroupId = work.Id };
        WorkspaceInfo deep = second with { GroupId = deepest.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, inner, deepest], top, deep));

        await model.Start();
        model.SelectedEntry = model.Entries[0];
        model.SelectedEntry = model.Entries[1];
        model.SelectedEntry = model.Entries[2];

        Assert.Equal([(object)work, inner, deepest, deep, top], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.Equal([0, 1, 2, 3, 1], model.Entries.Select(entry => entry.Depth));
        Assert.Equal(54, model.Entries[3].Indent.Left);

        model.SelectedEntry = model.Entries[1];
        Assert.Equal([(object)work, inner, top], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
    }

    [Fact]
    public async Task Entries_ShowWorkspacesWhoseGroupIsMissingAtTheTopLevel()
    {
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first with { GroupId = Guid.NewGuid() }));

        await model.Start();

        Assert.Equal(0, Assert.Single(model.Entries).Depth);
    }

    [Fact]
    public async Task TopBar_TurnsAccentBlueWhileTheFocusedWorkspaceHasAiWorking()
    {
        await model.Start();
        List<string?> changed = [];
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.False(model.IsAiWorking);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = first.Id, Working = true });
        Assert.True(model.IsAiWorking);
        Assert.Contains(nameof(HomeViewModel.IsAiWorking), changed);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = second.Id, Working = false });
        Assert.True(model.IsAiWorking);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = first.Id, Working = false });
        Assert.False(model.IsAiWorking);
    }

    [Fact]
    public async Task OpenedWorkspaces_StayOpenWhenYouLeaveReturnAndReload()
    {
        await model.Start();
        WorkspaceViewModel active = model.Active!;
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = first.Id, Working = true });

        model.Selected = second;
        model.Selected = first;
        Assert.Same(active, model.Active);
        Assert.True(model.Active!.IsAiWorking);

        await model.Start(first.Id);
        Assert.Same(active, model.Active);
        Assert.True(model.IsAiWorking);
    }

    [Fact]
    public async Task Start_StopsFollowingWorkspacesThatNoLongerExist()
    {
        await model.Start();
        WorkspaceViewModel gone = model.Active!;
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], second));

        await model.Start();
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = first.Id, Working = true });

        Assert.False(gone.IsAiWorking);
        Assert.NotSame(gone, model.Active);
    }

    [Fact]
    public async Task Entries_PutWorkspacesThatLoopFirstInTheirListWithTheLoopFlagAndFollowTheLoopEvents()
    {
        DateTimeOffset now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
        WorkspaceInfo newest = new() { Id = Guid.NewGuid(), Folder = "/work/aaa", LastUpdated = now };
        WorkspaceInfo older = new() { Id = Guid.NewGuid(), Folder = "/work/mmm", LastUpdated = now.AddDays(-1) };
        WorkspaceInfo oldest = new() { Id = Guid.NewGuid(), Folder = "/work/zzz", LastUpdated = now.AddDays(-2) };
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo inWork = new() { Id = Guid.NewGuid(), Folder = "/work/in", GroupId = work.Id, LastUpdated = now };
        WorkspaceInfo inWorkLooping = new() { Id = Guid.NewGuid(), Folder = "/work/out", GroupId = work.Id, LastUpdated = now.AddDays(-5) };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], newest, older, oldest, inWork, inWorkLooping) with { Looping = [oldest.Id, inWorkLooping.Id] });
        await model.Start();
        model.SelectedEntry = model.Entries[0];

        Assert.Equal([(object)work, inWorkLooping, inWork, oldest, newest, older], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.Equal([inWorkLooping, oldest], model.Entries.Where(entry => entry.IsLooping).Select(entry => entry.Workspace));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = older.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", IntervalSeconds = 60 }, StartedAt = now } });

        Assert.Equal([(object)work, inWorkLooping, inWork, older, oldest, newest], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = older.Id, Loop = new LoopInfo { Settings = new LoopSettings { Prompt = "x", IntervalSeconds = 60 }, StartedAt = now, Runs = 2 } });
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.LoopChanged, WorkspaceId = oldest.Id });

        Assert.Equal([(object)work, inWorkLooping, inWork, older, newest, oldest], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
        Assert.Equal([older], model.Entries.Where(entry => entry.IsWorkspace && entry.IsLooping && entry.Depth == 0).Select(entry => entry.Workspace));
    }
}
