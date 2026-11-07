namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceManagerTests
{
    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IFolderBrowser> folders = new();

    private readonly Mock<IEventHub> events = new();

    private readonly FakePrompter prompter = new();

    private readonly HostMock host;

    private readonly WorkspaceManager manager;

    public WorkspaceManagerTests()
    {
        host = new HostMock(prompter);
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog());
        folders.Setup(x => x.Exists(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        manager = new WorkspaceManager(store.Object, folders.Object, events.Object, host.Object);
    }

    private async Task<string> Session(string folder, AiService service = AiService.ClaudeCode) => await host.Object.CreateSession(folder, service, null, CancellationToken.None);

    [Fact]
    public async Task Open_ReadsTheFolderAndProviderOfEachWorkspaceFromItsSession()
    {
        string first = await Session("/work/b", AiService.Codex);
        string second = await Session("/work/a");
        Guid group = Guid.NewGuid();
        StoredWorkspace one = new() { Id = Guid.NewGuid(), SessionId = first, GroupId = group };
        StoredWorkspace two = new() { Id = Guid.NewGuid(), SessionId = second };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Groups = [new WorkspaceGroup { Id = group, Name = "Work" }], Workspaces = [one, two] });

        await manager.Open("workspaces.json", CancellationToken.None);

        store.Verify(x => x.Open("workspaces.json"), Times.Once);
        WorkspaceCatalog catalog = await manager.List(CancellationToken.None);
        Assert.Equal([new WorkspaceInfo { Id = one.Id, Folder = "/work/b", Service = AiService.Codex, SessionId = first, GroupId = group }, new WorkspaceInfo { Id = two.Id, Folder = "/work/a", Service = AiService.ClaudeCode, SessionId = second }], catalog.Workspaces.Select(workspace => workspace with { LastUpdated = null }));
        Assert.All(catalog.Workspaces, workspace => Assert.NotNull(workspace.LastUpdated));
        Assert.Single(catalog.Groups);
        store.Verify(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Open_GivesAWorkspaceAnOlderReleaseStoredWithoutASessionItsFirstOneAndStoresIt()
    {
        StoredWorkspace pending = new() { Id = Guid.NewGuid(), Folder = "/work/old", Service = AiService.Codex };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [pending] });
        StoredCatalog? saved = null;
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved = catalog).Returns(Task.CompletedTask);

        await manager.Open("workspaces.json", CancellationToken.None);

        WorkspaceInfo workspace = Assert.Single((await manager.List(CancellationToken.None)).Workspaces);
        Assert.Equal((pending.Id, "/work/old", AiService.Codex), (workspace.Id, workspace.Folder, workspace.Service));
        Assert.IsType<CodexSetup>(Assert.Single(prompter.Created));
        StoredWorkspace stored = Assert.Single(saved!.Workspaces);
        Assert.Equal((workspace.SessionId, null, null), (stored.SessionId, stored.Folder, stored.Service));
        events.Verify(x => x.Publish(It.IsAny<RemoteEvent>()), Times.Never);
    }

    [Fact]
    public async Task Open_KeepsAWorkspaceWhoseSessionIsGoneInTheFileButDoesNotListItAndKeepsItWhenSaving()
    {
        string known = await Session("/work/a");
        StoredWorkspace lost = new() { Id = Guid.NewGuid(), SessionId = "0123456789abcdef0123456789abcdef" };
        StoredWorkspace kept = new() { Id = Guid.NewGuid(), SessionId = known };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [lost, kept] });
        StoredCatalog? saved = null;
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved = catalog).Returns(Task.CompletedTask);
        await manager.Open("workspaces.json", CancellationToken.None);

        Assert.Equal([kept.Id], (await manager.List(CancellationToken.None)).Workspaces.Select(workspace => workspace.Id));
        await manager.CreateGroup("Work", null, CancellationToken.None);

        Assert.Equal([kept.Id, lost.Id], saved!.Workspaces.Select(workspace => workspace.Id));
    }

    [Fact]
    public async Task Open_GivesASessionAnOlderReleaseStoredWhatTheServerRunsSessionsWithAndClearsTheMark()
    {
        string session = await Session("/work/a");
        StoredWorkspace stored = new() { Id = Guid.NewGuid(), SessionId = session, Adopt = true };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [stored] });
        StoredCatalog? saved = null;
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved = catalog).Returns(Task.CompletedTask);

        await manager.Open("workspaces.json", CancellationToken.None);

        host.Mock.Verify(x => x.Adopt(session, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(Assert.Single(saved!.Workspaces).Adopt);
        Assert.Single((await manager.List(CancellationToken.None)).Workspaces);
        events.Verify(x => x.Publish(It.IsAny<RemoteEvent>()), Times.Never);
    }

    [Fact]
    public async Task Open_KeepsTheMarkOfASessionThatCannotBeAdoptedYetAndStillListsTheWorkspace()
    {
        string session = await Session("/work/a");
        StoredWorkspace stored = new() { Id = Guid.NewGuid(), SessionId = session, Adopt = true };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [stored] });
        host.Mock.Setup(x => x.Adopt(session, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The workspace folder does not exist."));
        StoredCatalog? saved = null;
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved = catalog).Returns(Task.CompletedTask);

        await manager.Open("workspaces.json", CancellationToken.None);
        await manager.CreateGroup("Work", null, CancellationToken.None);

        Assert.Single((await manager.List(CancellationToken.None)).Workspaces);
        Assert.True(Assert.Single(saved!.Workspaces).Adopt);
    }

    [Fact]
    public async Task Open_LeavesAPendingWorkspaceForNextTimeWhenItsSessionCannotBeMade()
    {
        StoredWorkspace pending = new() { Id = Guid.NewGuid(), Folder = "/work/old", Service = AiService.Local };
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [pending] });
        host.Mock.Setup(x => x.CreateSession("/work/old", AiService.Local, null, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Local models need a model for every capability."));

        await manager.Open("workspaces.json", CancellationToken.None);

        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
        store.Verify(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_StoresWorkspaceNamedAfterFolder()
    {
        WorkspaceInfo created = await manager.Create("/work/app/", AiService.ClaudeCode, null, null, CancellationToken.None);

        Assert.Equal("/work/app", created.Folder);
        Assert.Equal("app", created.Name);
        store.Verify(x => x.Save(It.Is<StoredCatalog>(catalog => catalog.Workspaces.Count == 1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Create_MakesTheSessionThatHoldsTheFolderAndProviderAndStoresOnlyItsIdentifier()
    {
        StoredCatalog? saved = null;
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved = catalog).Returns(Task.CompletedTask);

        WorkspaceInfo created = await manager.Create("/work/app", AiService.Codex, "sk-1", null, CancellationToken.None);

        CodexSetup setup = Assert.IsType<CodexSetup>(Assert.Single(prompter.Created));
        Assert.Equal(("/work/app", "sk-1"), (setup.WorkspacePath, setup.ApiKey));
        Assert.Equal((AiService.Codex, prompter.Sessions[0].Id), (created.Service, created.SessionId));
        StoredWorkspace stored = Assert.Single(saved!.Workspaces);
        Assert.Equal((created.Id, created.SessionId, null, null), (stored.Id, stored.SessionId, stored.Folder, stored.Service));
    }

    [Fact]
    public async Task Create_DoesNotAddTheWorkspaceWhenItsSessionCannotBeMade()
    {
        host.Mock.Setup(x => x.CreateSession(It.IsAny<string>(), It.IsAny<AiService>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The provider takes no key."));

        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/work/app", AiService.Local, "sk-1", null, CancellationToken.None));

        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
        store.Verify(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Create_DeletesTheSessionAgainWhenTheWorkspacesCannotBeStored()
    {
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None));

        Assert.Empty(prompter.Sessions);
        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Create_RejectsMissingFolder()
    {
        folders.Setup(x => x.Exists("/nope", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/nope", AiService.ClaudeCode, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsFolderAlreadyHostingWorkspace()
    {
        await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task List_KeepsCreationOrderAndRenameKeepsPlace()
    {
        await manager.Create("/work/zeta", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo alpha = await manager.Create("/work/Alpha", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Setup(x => x.Rename("/work/Alpha", "omega", It.IsAny<CancellationToken>())).ReturnsAsync("/work/omega");

        await manager.Rename(alpha.Id, "omega", CancellationToken.None);

        Assert.Equal(["zeta", "omega"], (await manager.List(CancellationToken.None)).Workspaces.Select(workspace => workspace.Name));
    }

    [Fact]
    public async Task Create_FilesTheWorkspaceInTheGivenGroupAndRefusesAGroupThatIsGoneBeforeMakingASession()
    {
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);

        WorkspaceInfo filed = await manager.Create("/work/a", AiService.ClaudeCode, null, group.Id, CancellationToken.None);
        await Assert.ThrowsAsync<RequestException>(() => manager.Create("/work/b", AiService.ClaudeCode, null, Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(group.Id, filed.GroupId);
        Assert.Single(prompter.Created);
    }

    [Fact]
    public async Task List_ReadsWhenEachSessionWasLastLoadedAgainSoTheOrderFollowsTheLatestWork()
    {
        WorkspaceInfo a = await manager.Create("/work/a", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo b = await manager.Create("/work/b", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo[] before = [.. (await manager.List(CancellationToken.None)).Workspaces];

        await using (IPromptSession loaded = await prompter.Load(a.SessionId!))
        {
        }

        WorkspaceInfo[] after = [.. (await manager.List(CancellationToken.None)).Workspaces];

        Assert.All(before, workspace => Assert.NotNull(workspace.LastUpdated));
        Assert.True(after.Single(workspace => workspace.Id == a.Id).LastUpdated >= before.Single(workspace => workspace.Id == a.Id).LastUpdated);
        Assert.True(after.Single(workspace => workspace.Id == a.Id).LastUpdated >= after.Single(workspace => workspace.Id == b.Id).LastUpdated);
    }

    [Fact]
    public async Task SetPrompts_StoresTheTrimmedOverridesWithTheWorkspaceAndClearsBlankOnes()
    {
        string session = await Session("/work/a");
        Guid id = Guid.NewGuid();
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(new StoredCatalog { Workspaces = [new StoredWorkspace { Id = id, SessionId = session, ReviewPrompt = "old review", ExpandPrompt = "old expand" }] });
        List<StoredCatalog> saved = [];
        store.Setup(x => x.Save(It.IsAny<StoredCatalog>(), It.IsAny<CancellationToken>())).Callback<StoredCatalog, CancellationToken>((catalog, _) => saved.Add(catalog)).Returns(Task.CompletedTask);
        await manager.Open("workspaces.json", CancellationToken.None);
        Assert.Equal(("old review", "old expand"), ((await manager.Get(id, CancellationToken.None)).ReviewPrompt, (await manager.Get(id, CancellationToken.None)).ExpandPrompt));

        WorkspaceInfo changed = await manager.SetPrompts(id, "  new review ", "   ", CancellationToken.None);
        WorkspaceInfo same = await manager.SetPrompts(id, "new review", null, CancellationToken.None);

        Assert.Equal(("new review", null), (changed.ReviewPrompt, changed.ExpandPrompt));
        Assert.Equal(changed, same);
        StoredWorkspace stored = Assert.Single(Assert.Single(saved).Workspaces);
        Assert.Equal(("new review", null), (stored.ReviewPrompt, stored.ExpandPrompt));
        events.Verify(x => x.Publish(It.IsAny<RemoteEvent>()), Times.Never);
    }

    [Fact]
    public async Task SetPrompts_RefusesAnUnknownWorkspace()
    {
        await manager.Open("workspaces.json", CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.SetPrompts(Guid.NewGuid(), "a", "b", CancellationToken.None));
    }

    [Fact]
    public async Task Move_PutsWorkspaceAtTheEndOfTheGroup()
    {
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        WorkspaceInfo a = await manager.Create("/work/a", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo b = await manager.Create("/work/b", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo c = await manager.Create("/work/c", AiService.ClaudeCode, null, null, CancellationToken.None);
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
        WorkspaceInfo workspace = await manager.Create("/work/old", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Setup(x => x.Rename("/work/old", "fresh", It.IsAny<CancellationToken>())).ReturnsAsync("/work/fresh");

        WorkspaceInfo renamed = await manager.Rename(workspace.Id, "fresh", CancellationToken.None);

        Assert.Equal(workspace.Id, renamed.Id);
        Assert.Equal("fresh", renamed.Name);
        Assert.Equal("fresh", (await manager.Get(workspace.Id, CancellationToken.None)).Name);
    }

    [Fact]
    public async Task Rename_MovesTheSessionToTheRenamedFolder()
    {
        WorkspaceInfo workspace = await manager.Create("/work/old", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Setup(x => x.Rename("/work/old", "fresh", It.IsAny<CancellationToken>())).ReturnsAsync("/work/fresh");

        await manager.Rename(workspace.Id, "fresh", CancellationToken.None);

        host.Mock.Verify(x => x.MoveSession(workspace.SessionId!, "/work/fresh", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Rename_PutsTheFolderBackWhenTheSessionCannotBeMoved()
    {
        WorkspaceInfo workspace = await manager.Create("/work/old", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Setup(x => x.Rename("/work/old", "fresh", It.IsAny<CancellationToken>())).ReturnsAsync("/work/fresh");
        host.Mock.Setup(x => x.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The session is in use."));

        await Assert.ThrowsAsync<RequestException>(() => manager.Rename(workspace.Id, "fresh", CancellationToken.None));

        folders.Verify(x => x.Rename("/work/fresh", "old", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("/work/old", (await manager.Get(workspace.Id, CancellationToken.None)).Folder);
    }

    [Fact]
    public async Task MoveToFolder_TellsTheSessionTheWorkspaceIsThereNowAndKeepsItsIdentifierPlaceAndGroup()
    {
        WorkspaceInfo first = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceInfo other = await manager.Create("/work/other", AiService.ClaudeCode, null, null, CancellationToken.None);

        WorkspaceInfo moved = await manager.MoveToFolder(first.Id, "/archive/app/", CancellationToken.None);

        Assert.Equal(("/archive/app", "app", first.Id, first.SessionId), (moved.Folder, moved.Name, moved.Id, moved.SessionId));
        host.Mock.Verify(x => x.MoveSession(first.SessionId!, "/archive/app", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal([first.Id, other.Id], (await manager.List(CancellationToken.None)).Workspaces.Select(workspace => workspace.Id));
        Assert.Equal("/archive/app", (await manager.Get(first.Id, CancellationToken.None)).Folder);
    }

    [Fact]
    public async Task MoveToFolder_StoresTheMoveAndAnnouncesIt()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Invocations.Clear();

        await manager.MoveToFolder(workspace.Id, "/archive/app", CancellationToken.None);

        store.Verify(x => x.Save(It.Is<StoredCatalog>(catalog => catalog.Workspaces.Single().Id == workspace.Id), It.IsAny<CancellationToken>()), Times.Exactly(2));
        VerifyAnnounced(2);
    }

    [Fact]
    public async Task MoveToFolder_ToTheFolderItIsInAlreadyChangesNothing()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        WorkspaceInfo moved = await manager.MoveToFolder(workspace.Id, "/work/app/", CancellationToken.None);

        Assert.Equal(workspace, moved);
        host.Mock.Verify(x => x.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MoveToFolder_KeepsEverythingWhenTheSessionCannotBeMoved()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        host.Mock.Setup(x => x.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The session is in use."));

        await Assert.ThrowsAsync<RequestException>(() => manager.MoveToFolder(workspace.Id, "/archive/app", CancellationToken.None));

        Assert.Equal("/work/app", (await manager.Get(workspace.Id, CancellationToken.None)).Folder);
    }

    [Fact]
    public async Task MoveToFolder_RefusesAFolderThatIsMissingOrAlreadyHostsAnotherWorkspaceOrAnUnknownWorkspace()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        await manager.Create("/work/other", AiService.ClaudeCode, null, null, CancellationToken.None);
        folders.Setup(x => x.Exists("/nope", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await Assert.ThrowsAsync<RequestException>(() => manager.MoveToFolder(workspace.Id, "/nope", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.MoveToFolder(workspace.Id, "/work/other", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => manager.MoveToFolder(Guid.NewGuid(), "/archive/app", CancellationToken.None));

        host.Mock.Verify(x => x.MoveSession(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("/work/app", (await manager.Get(workspace.Id, CancellationToken.None)).Folder);
    }

    [Fact]
    public async Task Rename_RejectsWhenAnotherWorkspaceLivesInsideFolder()
    {
        WorkspaceInfo outer = await manager.Create("/work/outer", AiService.ClaudeCode, null, null, CancellationToken.None);
        await manager.Create("/work/outer/inner", AiService.ClaudeCode, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Rename(outer.Id, "x", CancellationToken.None));
        folders.Verify(x => x.Rename(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WithFolderRemovesFolderWorkspaceAndAttachments()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        await manager.Delete(workspace.Id, true, CancellationToken.None);

        folders.Verify(x => x.Delete("/work/app", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(x => x.DeleteAttachments(workspace.Id), Times.Once);
        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Delete_WithoutFolderKeepsFolder()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        await manager.Delete(workspace.Id, false, CancellationToken.None);

        folders.Verify(x => x.Delete(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty((await manager.List(CancellationToken.None)).Workspaces);
    }

    [Fact]
    public async Task Delete_WithFolderRejectsFolderSharedWithAnotherWorkspace()
    {
        WorkspaceInfo outer = await manager.Create("/work/outer", AiService.ClaudeCode, null, null, CancellationToken.None);
        await manager.Create("/work/outer/inner", AiService.ClaudeCode, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Delete(outer.Id, true, CancellationToken.None));
        Assert.Equal(2, (await manager.List(CancellationToken.None)).Workspaces.Count);
    }

    [Fact]
    public async Task Create_StoresTheProviderTheWorkspaceRunsOnForGood()
    {
        WorkspaceInfo codex = await manager.Create("/work/codex", AiService.Codex, null, null, CancellationToken.None);
        WorkspaceInfo local = await manager.Create("/work/local", AiService.Local, null, null, CancellationToken.None);

        Assert.Equal((AiService.Codex, AiService.Local), (codex.Service, local.Service));
        Assert.Equal(AiService.Codex, (await manager.Get(codex.Id, CancellationToken.None)).Service);
    }

    [Fact]
    public async Task NewWorkspace_StartsOnClaudeCode()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        Assert.Equal(AiService.ClaudeCode, workspace.Service);
        Assert.Matches("^[0-9a-f]{32}$", workspace.SessionId);
    }

    [Fact]
    public async Task AttachSession_StoresIt()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        string attached = Guid.NewGuid().ToString("N");

        await manager.AttachSession(workspace.Id, attached, CancellationToken.None);

        Assert.Equal(attached, (await manager.Get(workspace.Id, CancellationToken.None)).SessionId);
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
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);

        Assert.Equal(group.Id, (await manager.Move(workspace.Id, group.Id, CancellationToken.None)).GroupId);
        Assert.Null((await manager.Move(workspace.Id, null, CancellationToken.None)).GroupId);
    }

    [Fact]
    public async Task Move_RejectsUnknownGroup()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => manager.Move(workspace.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task DeleteGroup_KeepsItsWorkspacesAndMovesThemOut()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
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
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
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

    private void VerifyAnnounced(int times) => events.Verify(x => x.Publish(It.Is<RemoteEvent>(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkspacesChanged && remoteEvent.WorkspaceId == Guid.Empty)), Times.Exactly(times));

    [Fact]
    public async Task EveryChangeToTheWorkspacesOrGroupsIsAnnouncedToTheClients()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);
        WorkspaceGroup group = await manager.CreateGroup("Work", null, CancellationToken.None);
        VerifyAnnounced(2);

        await manager.Move(workspace.Id, group.Id, CancellationToken.None);
        await manager.RenameGroup(group.Id, "Play", CancellationToken.None);
        await manager.ShiftGroup(group.Id, 1, CancellationToken.None);
        VerifyAnnounced(5);

        await manager.DeleteGroup(group.Id, CancellationToken.None);
        await manager.Delete(workspace.Id, false, CancellationToken.None);
        VerifyAnnounced(7);
    }

    [Fact]
    public async Task AttachingASessionIsNotAnnouncedAndARefusalIsNot()
    {
        WorkspaceInfo workspace = await manager.Create("/work/app", AiService.ClaudeCode, null, null, CancellationToken.None);

        await manager.AttachSession(workspace.Id, Guid.NewGuid().ToString("N"), CancellationToken.None);
        await Assert.ThrowsAsync<RequestException>(() => manager.CreateGroup("  ", null, CancellationToken.None));

        VerifyAnnounced(1);
    }
}
