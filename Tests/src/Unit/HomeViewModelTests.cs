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
        connection.Setup(x => x.GetHistory(It.IsAny<Guid>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        model = new HomeViewModel(connection.Object);
    }

    private WorkspaceCatalog Catalog(WorkspaceGroup[] groups, params WorkspaceInfo[] workspaces) => new() { Groups = groups, Workspaces = workspaces };

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
        model.SelectedEntry = model.Entries.First(entry => entry.Group == clients);

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
        Assert.Equal("Keep working", model.CancelEditText);
        connection.Verify(x => x.StopWorkspace(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        model.ConfirmHaltCommand.Execute(null);
        await Task.Delay(50);
        Assert.False(model.IsEditing);
        connection.Verify(x => x.StopWorkspace(first.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PauseAndResume_AreOnlyAvailableInTheirStatesAndGoStraightToTheServer()
    {
        await model.Start();
        Assert.False(model.PauseCommand.CanExecute(null));
        Working(first, true);
        Assert.True(model.PauseCommand.CanExecute(null));
        Assert.False(model.ResumeCommand.CanExecute(null));

        model.PauseCommand.Execute(null);
        await Task.Delay(50);
        connection.Verify(x => x.PauseWorkspace(first.Id, It.IsAny<CancellationToken>()), Times.Once);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = first.Id, State = WorkspaceState.Paused });

        Assert.False(model.PauseCommand.CanExecute(null));
        Assert.True(model.ResumeCommand.CanExecute(null));
        Assert.True(model.BeginStopCommand.CanExecute(null));
        model.ResumeCommand.Execute(null);
        await Task.Delay(50);
        connection.Verify(x => x.ResumeWorkspace(first.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IsPaused_IsOnlyTrueWhileTheFocusedWorkspaceIsPausedAndTellsTheHeaderSo()
    {
        await model.Start();
        List<string?> changed = [];
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Working(first, true);
        Assert.True(model.IsWorking);
        Assert.False(model.IsPaused);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = first.Id, State = WorkspaceState.Paused });

        Assert.True(model.IsPaused);
        Assert.True(model.IsWorking);
        Assert.Contains(nameof(HomeViewModel.IsPaused), changed);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = first.Id, State = WorkspaceState.Working });

        Assert.False(model.IsPaused);
        Assert.True(model.IsWorking);
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

        Assert.Equal([(object)group, first with { GroupId = group.Id }, second, added], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));
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
        await Changed(Catalog([], first with { Folder = "/work/renamed", Model = AiModel.Opus, Effort = AiEffort.Low }, second));

        Assert.NotSame(renamed, model.Active);
        Assert.Equal(AiModel.Opus, model.Active?.Workspace.Model);
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

        collectionChanges.Clear();
        await Changed(Catalog([], second, first, third, added));

        Assert.Equal(["Move"], collectionChanges);
        Assert.Same(selectedBefore, model.SelectedEntry);
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

        Assert.False(model.Entries.First(entry => entry.Group == clients).IsCollapsed);
        Assert.Contains(model.Entries, entry => entry.Workspace == acme);
    }

    [Fact]
    public async Task ShiftCommands_AskTheServerAndShowTheNewOrder()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        await model.Start();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([group], second, first));

        model.ShiftWorkspaceUpCommand.Execute(second);

        connection.Verify(x => x.ShiftWorkspace(second.Id, -1, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([group, second, first], model.Entries.Select(entry => (object?)entry.Group ?? entry.Workspace));

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

        Assert.Equal(work, model.Entries[0].Group);
        Assert.Equal(grouped, model.Entries[1].Workspace);
        Assert.Equal(1, model.Entries[1].Depth);
        Assert.Equal(second, model.Entries[2].Workspace);
        Assert.Equal(0, model.Entries[2].Depth);
        Assert.Equal(grouped, model.Selected);
    }

    [Fact]
    public async Task TappingAGroupEntry_CollapsesAndExpandsIt()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first with { GroupId = work.Id }, second));
        await model.Start();

        model.SelectedEntry = model.Entries[0];
        Assert.Equal(2, model.Entries.Count);
        Assert.True(model.Entries[0].IsCollapsed);
        Assert.StartsWith("▸", model.Entries[0].Text);

        model.SelectedEntry = model.Entries[0];
        Assert.Equal(3, model.Entries.Count);
        Assert.StartsWith("▾", model.Entries[0].Text);
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
}
