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
    public async Task Selecting_ShowsTheUsageOfTheFocusedWorkspacesServiceAndClosesTheUsageView()
    {
        WorkspaceInfo codex = second with { Service = AiService.Codex, Model = AiModel.Terra, Effort = AiEffort.Medium };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], first, codex));
        connection.Setup(x => x.GetUsage(It.IsAny<AiService>(), It.IsAny<CancellationToken>())).ReturnsAsync((AiService service, CancellationToken _) => new UsageStatus { Service = service });
        await model.Start();
        model.ToggleUsageCommand.Execute(null);
        Assert.True(model.IsUsageOpen);

        model.Selected = codex;
        await Task.Delay(50);

        Assert.False(model.IsUsageOpen);
        Assert.Equal("OpenAI Codex", model.Usage.ServiceName);
        connection.Verify(x => x.GetUsage(AiService.ClaudeCode, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.GetUsage(AiService.Codex, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task MoveWorkspace_OffersTheGroupTreeAndMovesIt()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, inner], first, second));
        connection.Setup(x => x.MoveWorkspace(second.Id, inner.Id, It.IsAny<CancellationToken>())).ReturnsAsync(second with { GroupId = inner.Id });
        await model.Start();
        WorkspaceViewModel active = model.Active!;

        model.BeginMoveWorkspaceCommand.Execute(second);
        Assert.True(model.IsMovingWorkspace);
        Assert.Equal(["Top level", "Work", "Work / Inner"], model.Destinations.Select(choice => choice.Name));
        Assert.Null(model.Destination?.Id);
        model.Destination = model.Destinations[2];
        model.ConfirmMoveCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.MoveWorkspace(second.Id, inner.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.IsEditing);
        Assert.Same(active, model.Active);
    }

    [Fact]
    public async Task MoveGroup_LeavesOutItselfAndItsDescendantsAndMovesIt()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceGroup inner = new() { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id };
        WorkspaceGroup play = new() { Id = Guid.NewGuid(), Name = "Play" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work, inner, play], first, second));
        connection.Setup(x => x.MoveGroup(work.Id, play.Id, It.IsAny<CancellationToken>())).ReturnsAsync(work with { ParentId = play.Id });
        await model.Start();

        model.BeginMoveGroupCommand.Execute(work);
        Assert.True(model.IsMovingGroup);
        Assert.Equal(["Top level", "Play"], model.Destinations.Select(choice => choice.Name));
        model.Destination = model.Destinations[1];
        model.ConfirmMoveCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.MoveGroup(work.Id, play.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task NewGroup_FromAGroupCreatesItInsideThatGroup()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first, second));
        connection.Setup(x => x.CreateGroup("Inner", work.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceGroup { Id = Guid.NewGuid(), Name = "Inner", ParentId = work.Id });
        await model.Start();

        model.BeginNewGroupCommand.Execute(work);
        Assert.Contains("inside Work", model.NewGroupHint);
        model.RenameText = "Inner";
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.CreateGroup("Inner", work.Id, It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task NewGroup_CreatesItAndKeepsDrawerOpen()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.CreateGroup("Work", null, It.IsAny<CancellationToken>())).ReturnsAsync(work);
        await model.Start();
        model.IsDrawerOpen = true;
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first, second));

        model.BeginNewGroupCommand.Execute(null);
        Assert.True(model.IsCreatingGroup);
        model.RenameText = "Work";
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(100);

        Assert.False(model.IsEditing);
        Assert.True(model.IsDrawerOpen);
        Assert.Equal(work, model.Entries[0].Group);
        Assert.Equal(first, model.Selected);
    }

    [Fact]
    public async Task NewGroup_ClosesTheDialogEvenWithNoWorkspaces()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([]));
        connection.Setup(x => x.CreateGroup("Work", null, It.IsAny<CancellationToken>())).ReturnsAsync(work);
        await model.Start();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work]));

        model.BeginNewGroupCommand.Execute(null);
        model.RenameText = "Work";
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(100);

        Assert.False(model.IsEditing);
        Assert.True(model.IsDrawerOpen);
        Assert.Equal(work, Assert.Single(model.Entries).Group);
    }

    [Fact]
    public async Task NewGroup_NeedsAName()
    {
        await model.Start();

        model.BeginNewGroupCommand.Execute(null);
        model.ConfirmNewGroupCommand.Execute(null);
        await Task.Delay(50);

        Assert.True(model.IsCreatingGroup);
        Assert.Equal("Enter a name.", model.Error);
        connection.Verify(x => x.CreateGroup(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RenameGroup_AppliesNewName()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first, second));
        connection.Setup(x => x.RenameGroup(work.Id, "Play", It.IsAny<CancellationToken>())).ReturnsAsync(work with { Name = "Play" });
        await model.Start();

        model.BeginRenameGroupCommand.Execute(work);
        Assert.True(model.IsRenamingGroup);
        Assert.Equal("Work", model.RenameText);
        model.RenameText = "Play";
        model.ConfirmRenameGroupCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.RenameGroup(work.Id, "Play", It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task DeleteGroup_ConfirmsThenDeletes()
    {
        WorkspaceGroup work = new() { Id = Guid.NewGuid(), Name = "Work" };
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([work], first, second));
        await model.Start();

        model.BeginDeleteGroupCommand.Execute(work);
        Assert.True(model.IsDeletingGroup);
        Assert.Equal("Work", model.EditingGroupName);
        model.ConfirmDeleteGroupCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.DeleteGroup(work.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task Rename_RenamesSelectedAndRefocusesIt()
    {
        WorkspaceInfo renamed = first with { Folder = "/work/c" };
        connection.Setup(x => x.RenameWorkspace(first.Id, "c", It.IsAny<CancellationToken>())).ReturnsAsync(renamed);
        await model.Start();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], renamed, second));

        model.BeginRenameCommand.Execute(null);
        Assert.True(model.IsRenaming);
        Assert.Equal("a", model.RenameText);
        model.RenameText = "c";
        model.ConfirmRenameCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal(renamed, model.Selected);
        Assert.False(model.IsEditing);
    }

    [Fact]
    public async Task Delete_DefaultsToDeletingFolderAndMovesToNextWorkspace()
    {
        await model.Start();
        connection.Setup(x => x.ListWorkspaces(It.IsAny<CancellationToken>())).ReturnsAsync(Catalog([], second));

        model.BeginDeleteCommand.Execute(null);
        Assert.True(model.DeleteFolder);
        model.ConfirmDeleteCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.DeleteWorkspace(first.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(second, model.Selected);
    }

    [Fact]
    public async Task Delete_CanKeepFolder()
    {
        await model.Start();

        model.BeginDeleteCommand.Execute(null);
        model.DeleteFolder = false;
        model.ConfirmDeleteCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.DeleteWorkspace(first.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_FailureKeepsPanelOpenWithError()
    {
        connection.Setup(x => x.DeleteWorkspace(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("busy"));
        await model.Start();

        model.BeginDeleteCommand.Execute(null);
        model.ConfirmDeleteCommand.Execute(null);
        await Task.Delay(100);

        Assert.True(model.IsDeleting);
        Assert.Equal("busy", model.Error);
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
