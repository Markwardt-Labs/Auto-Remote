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
