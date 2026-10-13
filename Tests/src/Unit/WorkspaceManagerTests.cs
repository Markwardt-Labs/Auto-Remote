namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceManagerTests
{
    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IFolderBrowser> folders = new();

    private readonly WorkspaceManager manager;

    public WorkspaceManagerTests()
    {
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [] });
        folders.Setup(x => x.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        manager = new WorkspaceManager(store.Object, folders.Object);
    }

    [Fact]
    public async Task Open_LoadsStoredWorkspaces()
    {
        WorkspaceInfo stored = new() { Id = Guid.NewGuid(), Folder = "/work/b" };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [stored] });

        await manager.Open("workspaces.json", CancellationToken.None);

        store.Verify(x => x.Open("workspaces.json"), Times.Once);
        Assert.Equal([stored], (await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Create_StoresWorkspaceNamedAfterFolder()
    {
        WorkspaceInfo created = await manager.Create("/work/app/", CancellationToken.None);

        Assert.Equal("/work/app", created.Folder);
        Assert.Equal("app", created.Name);
        store.Verify(x => x.Save(It.Is<WorkspaceCatalog>(catalog => catalog.Workspaces.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_RejectsMissingFolder()
    {
        folders.Setup(x => x.Exists("/nope", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/nope", CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsFolderAlreadyHostingWorkspace()
    {
        await manager.Create("/work/app", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/work/app", CancellationToken.None));
    }

    [Fact]
    public async Task List_KeepsCreationOrderAndRenameKeepsPlace()
    {
        await manager.Create("/work/zeta", CancellationToken.None);
        WorkspaceInfo alpha = await manager.Create("/work/Alpha", CancellationToken.None);
        folders.Setup(x => x.Rename("/work/Alpha", "omega", It.IsAny<CancellationToken>())).ReturnsAsync("/work/omega");

        await manager.Rename(alpha.Id, "omega", CancellationToken.None);

        Assert.Equal(["zeta", "omega"], (await manager.List(CancellationToken.None)).Workspaces.Select(workspace => workspace.Name));
    }

    [Fact]
    public async Task ShiftWorkspace_MovesAmongWorkspacesOfTheSameGroupAndStopsAtTheEnds()
    {
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        WorkspaceInfo a = await manager.Create("/work/a", CancellationToken.None);
        WorkspaceInfo b = await manager.Create("/work/b", CancellationToken.None);
        WorkspaceInfo c = await manager.Create("/work/c", CancellationToken.None);
        WorkspaceInfo d = await manager.Create("/work/d", CancellationToken.None);
        await manager.Move(b.Id, group.Id, CancellationToken.None);
        await manager.Move(d.Id, group.Id, CancellationToken.None);

        await manager.ShiftWorkspace(c.Id, -1, CancellationToken.None);
        Assert.Equal(["c", "a"], await Names(null));

        await manager.ShiftWorkspace(d.Id, -1, CancellationToken.None);
        Assert.Equal(["d", "b"], await Names(group.Id));

        await manager.ShiftWorkspace(d.Id, -1, CancellationToken.None);
        await manager.ShiftWorkspace(a.Id, 5, CancellationToken.None);
        Assert.Equal(["d", "b"], await Names(group.Id));
        Assert.Equal(["c", "a"], await Names(null));

        await manager.ShiftWorkspace(c.Id, 1, CancellationToken.None);
        Assert.Equal(["a", "c"], await Names(null));
        store.Verify(x => x.Save(It.IsAny<WorkspaceCatalog>(), It.IsAny<CancellationToken>()), Times.AtLeast(8));
        await Assert.ThrowsAsync<RequestException>(() => manager.ShiftWorkspace(Guid.NewGuid(), 1, CancellationToken.None));
    }

    [Fact]
    public async Task Move_PutsWorkspaceAtTheEndOfTheGroup()
    {
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        WorkspaceInfo a = await manager.Create("/work/a", CancellationToken.None);
        WorkspaceInfo b = await manager.Create("/work/b", CancellationToken.None);
        WorkspaceInfo c = await manager.Create("/work/c", CancellationToken.None);
        await manager.Move(c.Id, group.Id, CancellationToken.None);

        await manager.Move(a.Id, group.Id, CancellationToken.None);

        Assert.Equal(["c", "a"], await Names(group.Id));
        Assert.Equal(["b"], await Names(null));
    }

    [Fact]
    public async Task ShiftGroup_MovesAmongGroupsWithTheSameParent()
    {
        WorkspaceGroup first = await manager.CreateGroup("First", null, CancellationToken.None);
        WorkspaceGroup second = await manager.CreateGroup("Second", null, CancellationToken.None);
        WorkspaceGroup inner = await manager.CreateGroup("Inner", first.Id, CancellationToken.None);
        WorkspaceGroup third = await manager.CreateGroup("Third", null, CancellationToken.None);

        await manager.ShiftGroup(third.Id, -1, CancellationToken.None);
        await manager.ShiftGroup(first.Id, 1, CancellationToken.None);
        await manager.ShiftGroup(inner.Id, 1, CancellationToken.None);

        WorkspaceCatalog catalog = await manager.List(CancellationToken.None);
        Assert.Equal(["Third", "First", "Second"], catalog.Groups.Where(group => group.ParentId is null).Select(group => group.Name));
        Assert.Equal(["Inner"], catalog.Groups.Where(group => group.ParentId == first.Id).Select(group => group.Name));
        await Assert.ThrowsAsync<RequestException>(() => manager.ShiftGroup(Guid.NewGuid(), 1, CancellationToken.None));
    }

    [Fact]
    public async Task Rename_RenamesFolderAndUpdatesWorkspace()
    {
        WorkspaceInfo workspace = await manager.Create("/work/old", CancellationToken.None);
        folders.Setup(x => x.Rename("/work/old", "fresh", It.IsAny<CancellationToken>())).ReturnsAsync("/work/fresh");

        WorkspaceInfo renamed = await manager.Rename(workspace.Id, "fresh", CancellationToken.None);

        Assert.Equal(workspace.Id, renamed.Id);
        Assert.Equal("fresh", renamed.Name);
        Assert.Equal("fresh", (await manager.Get(workspace.Id, CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Rename_RejectsWhenAnotherWorkspaceLivesInsideFolder()
    {
        WorkspaceInfo outer = await manager.Create("/work/outer", CancellationToken.None);
        await manager.Create("/work/outer/inner", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Rename(outer.Id, "x", CancellationToken.None));
        folders.Verify(x => x.Rename(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WithFolderRemovesFolderWorkspaceAndHistory()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        await manager.Delete(workspace.Id, true, CancellationToken.None);

        folders.Verify(x => x.Delete("/work/app", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(x => x.DeleteHistory(workspace.Id), Times.Once);
        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Delete_WithoutFolderKeepsFolder()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        await manager.Delete(workspace.Id, false, CancellationToken.None);

        folders.Verify(x => x.Delete(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Delete_WithFolderRejectsFolderSharedWithAnotherWorkspace()
    {
        WorkspaceInfo outer = await manager.Create("/work/outer", CancellationToken.None);
        await manager.Create("/work/outer/inner", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Delete(outer.Id, true, CancellationToken.None));
        Assert.Equal(2, (await manager.List(CancellationToken.None)).Workspaces.Count);
    }

    [Fact]
    public async Task Configure_StoresModelAndEffort()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        WorkspaceInfo updated = await manager.Configure(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.Low, CancellationToken.None);

        Assert.Equal(AiModel.Opus, updated.Model);
        Assert.Equal(AiEffort.Low, (await manager.Get(workspace.Id, CancellationToken.None)).Effort);
    }

    [Fact]
    public async Task Configure_SwitchingServiceStoresItAndDetachesTheAiSession()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);
        await manager.AttachAiSession(workspace.Id, Guid.NewGuid(), CancellationToken.None);

        WorkspaceInfo kept = await manager.Configure(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.Low, CancellationToken.None);
        WorkspaceInfo switched = await manager.Configure(workspace.Id, AiService.Codex, AiModel.Sol, AiEffort.Medium, CancellationToken.None);

        Assert.NotNull(kept.AiSessionId);
        Assert.Equal((AiService.Codex, AiModel.Sol, AiEffort.Medium), (switched.Service, switched.Model, switched.Effort));
        Assert.Null(switched.AiSessionId);
    }

    [Theory]
    [InlineData(AiService.Codex, AiModel.Opus)]
    [InlineData(AiService.ClaudeCode, AiModel.Terra)]
    public async Task Configure_RefusesAModelTheServiceDoesNotOffer(AiService service, AiModel model)
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Configure(workspace.Id, service, model, AiEffort.High, CancellationToken.None));
        Assert.Equal(AiModel.Sonnet, (await manager.Get(workspace.Id, CancellationToken.None)).Model);
    }

    [Fact]
    public async Task NewWorkspace_DefaultsToSonnetOnHigh()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        Assert.Equal(AiService.ClaudeCode, workspace.Service);
        Assert.Equal(AiModel.Sonnet, workspace.Model);
        Assert.Equal(AiEffort.High, workspace.Effort);
        Assert.Null(workspace.AiSessionId);
    }

    [Fact]
    public async Task AttachAiSession_StoresIt()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);
        Guid attached = Guid.NewGuid();

        await manager.AttachAiSession(workspace.Id, attached, CancellationToken.None);

        Assert.Equal(attached, (await manager.Get(workspace.Id, CancellationToken.None)).AiSessionId);
    }

    [Fact]
    public async Task Groups_CreateRenameAndListInCreationOrder()
    {
        WorkspaceGroup zeta = await manager.CreateGroup(" Zeta ", null, CancellationToken.None);
        await manager.CreateGroup("alpha", null, CancellationToken.None);
        await manager.RenameGroup(zeta.Id, "Omega", CancellationToken.None);

        Assert.Equal(["Omega", "alpha"], (await manager.List(CancellationToken.None)).Groups.Select(group => group.Name));
    }

    [Fact]
    public async Task Groups_RejectEmptyAndDuplicateNames()
    {
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        WorkspaceGroup other = await manager.CreateGroup("Play", null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.CreateGroup("  ", null, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.CreateGroup("work", null, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.RenameGroup(other.Id, "WORK", CancellationToken.None));
        Assert.Equal("Work", (await manager.RenameGroup(group.Id, "Work", CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Groups_NestAndOnlyNeedUniqueNamesAmongSiblings()
    {
        WorkspaceGroup work = await manager.CreateGroup("Work", null, CancellationToken.None);
        WorkspaceGroup play = await manager.CreateGroup("Play", null, CancellationToken.None);
        WorkspaceGroup inner = await manager.CreateGroup("Play", work.Id, CancellationToken.None);

        Assert.Equal(work.Id, inner.ParentId);
        await Assert.ThrowsAsync<RequestException>(() => manager.CreateGroup("play", work.Id, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.CreateGroup("x", Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.MoveGroup(inner.Id, null, CancellationToken.None));
        Assert.Equal(play.Id, (await manager.MoveGroup(inner.Id, play.Id, CancellationToken.None)).ParentId);
    }

    [Fact]
    public async Task MoveGroup_RefusesItselfAndItsDescendants()
    {
        WorkspaceGroup outer = await manager.CreateGroup("Outer", null, CancellationToken.None);
        WorkspaceGroup inner = await manager.CreateGroup("Inner", outer.Id, CancellationToken.None);
        WorkspaceGroup deepest = await manager.CreateGroup("Deepest", inner.Id, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.MoveGroup(outer.Id, outer.Id, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.MoveGroup(outer.Id, deepest.Id, CancellationToken.None));
        Assert.Null((await manager.MoveGroup(deepest.Id, null, CancellationToken.None)).ParentId);
    }

    [Fact]
    public async Task Move_PutsWorkspaceInGroupAndBackOut()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);

        Assert.Equal(group.Id, (await manager.Move(workspace.Id, group.Id, CancellationToken.None)).GroupId);
        Assert.Null((await manager.Move(workspace.Id, null, CancellationToken.None)).GroupId);
    }

    [Fact]
    public async Task Move_RejectsUnknownGroup()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Move(workspace.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteGroup_KeepsItsWorkspacesAndMovesThemOut()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        await manager.Move(workspace.Id, group.Id, CancellationToken.None);

        await manager.DeleteGroup(group.Id, CancellationToken.None);

        WorkspaceCatalog catalog = await manager.List(CancellationToken.None);
        Assert.Empty(catalog.Groups);
        Assert.Null(Assert.Single(catalog.Workspaces).GroupId);
        folders.Verify(x => x.Delete(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteGroup_MovesItsContentsIntoItsParent()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", CancellationToken.None);
        WorkspaceGroup outer = await manager.CreateGroup("Outer", null, CancellationToken.None);
        WorkspaceGroup middle = await manager.CreateGroup("Middle", outer.Id, CancellationToken.None);
        WorkspaceGroup inner = await manager.CreateGroup("Inner", middle.Id, CancellationToken.None);
        await manager.Move(workspace.Id, middle.Id, CancellationToken.None);

        await manager.DeleteGroup(middle.Id, CancellationToken.None);

        WorkspaceCatalog catalog = await manager.List(CancellationToken.None);
        Assert.Equal(outer.Id, catalog.Groups.Single(group => group.Id == inner.Id).ParentId);
        Assert.Equal(outer.Id, Assert.Single(catalog.Workspaces).GroupId);
    }

    [Fact]
    public async Task Groups_ThrowForUnknownGroup()
    {
        await Assert.ThrowsAsync<RequestException>(() => manager.RenameGroup(Guid.NewGuid(), "x", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.DeleteGroup(Guid.NewGuid(), CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.MoveGroup(Guid.NewGuid(), null, CancellationToken.None));
    }

    [Fact]
    public async Task Get_ThrowsForUnknownWorkspace() => await Assert.ThrowsAsync<RequestException>(() => manager.Get(Guid.NewGuid(), CancellationToken.None));

    private async Task<IReadOnlyList<string>> Names(Guid? groupId) => [.. (await manager.List(CancellationToken.None)).Workspaces.Where(workspace => workspace.GroupId == groupId).Select(workspace => workspace.Name)];
}
