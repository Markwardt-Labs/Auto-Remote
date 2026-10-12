namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class RequestHandlerTests
{
    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IFolderBrowser> folders = new();

    private readonly Mock<IWorkspaceFileBrowser> files = new();

    private readonly Mock<IGitService> git = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IScriptRunner> scripts = new();

    private readonly Mock<IUsageService> usage = new();

    private readonly Mock<IEventHub> events = new();

    private readonly object owner = new();

    private readonly RequestHandler handler;

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    public RequestHandlerTests()
    {
        versioning.Setup(x => x.Perform(It.IsAny<WorkspaceInfo>(), It.IsAny<bool>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns((WorkspaceInfo _, bool _, Func<CancellationToken, Task> action, CancellationToken token, bool _) => action(token));
        handler = new RequestHandler(workspaces.Object, folders.Object, files.Object, git.Object, versioning.Object, prompts.Object, scripts.Object, usage.Object, events.Object);
    }

    [Fact]
    public async Task Open_ConfiguresPromptsAndOpensWorkspaces()
    {
        ServerConfig config = new() { Port = 1, Key = "k", DataFolder = "d" };

        await handler.Open(config, CancellationToken.None);

        prompts.Verify(x => x.Configure(config, CancellationToken.None), Times.Once);
        scripts.Verify(x => x.Configure(config, CancellationToken.None), Times.Once);
        folders.Verify(x => x.Configure(config.DefaultFolder), Times.Once);
        workspaces.Verify(x => x.Open(Path.Combine("d", "workspaces.json"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ListWorkspaces_ReturnsWorkspacesWithRequestId()
    {
        workspaces.Setup(x => x.List(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 7, Command = RemoteCommand.ListWorkspaces }, owner, CancellationToken.None);

        Assert.Equal(7, response.Id);
        Assert.Equal([workspace], response.Catalog?.Workspaces);
    }

    [Fact]
    public async Task Handle_CreateWorkspace_UsesPath()
    {
        workspaces.Setup(x => x.Create("/work/app", It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/work/app" }, owner, CancellationToken.None);

        Assert.Equal(workspace, response.Workspace);
    }

    [Fact]
    public async Task Handle_MissingArgument_ReportsError()
    {
        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace }, owner, CancellationToken.None);

        Assert.Equal(1, response.Id);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task Handle_ManagerFailure_BecomesErrorResponse()
    {
        workspaces.Setup(x => x.Create(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("taken"));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/x" }, owner, CancellationToken.None);

        Assert.Equal("taken", response.Error);
    }

    [Theory]
    [InlineData(RemoteCommand.RenameWorkspace)]
    [InlineData(RemoteCommand.DeleteWorkspace)]
    public async Task Handle_RenameOrDeleteWhileBusy_IsRefused(RemoteCommand command)
    {
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = command, WorkspaceId = workspace.Id, Name = "n" }, owner, CancellationToken.None);

        Assert.NotNull(response.Error);
        workspaces.Verify(x => x.Delete(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        workspaces.Verify(x => x.Rename(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_DeleteWorkspace_PassesFolderChoice()
    {
        await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = workspace.Id, DeleteFolder = false }, owner, CancellationToken.None);

        workspaces.Verify(x => x.Delete(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_ListFiles_DefaultsToWorkspaceRoot()
    {
        WorkspaceFiles listing = new() { Path = string.Empty, Entries = [] };
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        files.Setup(x => x.List(workspace, string.Empty, false, It.IsAny<CancellationToken>())).ReturnsAsync(listing);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.ListFiles, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal(listing, response.Files);
    }

    [Fact]
    public async Task Handle_UploadAttachment_AnswersTheStoredPath()
    {
        prompts.Setup(x => x.Attach(workspace.Id, "a.txt", "AQ==", It.IsAny<CancellationToken>())).ReturnsAsync("/srv/a.txt");

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.UploadAttachment, WorkspaceId = workspace.Id, Name = "a.txt", Data = "AQ==" }, owner, CancellationToken.None);

        Assert.Equal("/srv/a.txt", response.AttachmentPath);
    }

    [Fact]
    public async Task Handle_Prompt_AnswersAtOnceAfterHandingTheTextOver()
    {
        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "go" }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        prompts.Verify(x => x.Prompt(workspace.Id, "go", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_GetHistory_PassesTheChunkBounds()
    {
        ChatHistory chunk = new() { Messages = [], Start = 4, Working = true };
        prompts.Setup(x => x.History(workspace.Id, 9, 5, It.IsAny<CancellationToken>())).ReturnsAsync(chunk);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetHistory, WorkspaceId = workspace.Id, Before = 9, Limit = 5 }, owner, CancellationToken.None);

        Assert.Equal(chunk, response.History);
    }

    [Fact]
    public async Task Handle_WatchFiles_ReplacesTheConnectionsWatchAndPublishesChanges()
    {
        Mock<IDisposable> first = new();
        Mock<IDisposable> second = new();
        Action<string>? changed = null;
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        files.Setup(x => x.Watch(workspace, "src", It.IsAny<Action<string>>())).Callback<WorkspaceInfo, string, Action<string>>((_, _, callback) => changed = callback).Returns(first.Object);

        await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.WatchFiles, WorkspaceId = workspace.Id, Paths = ["src"] }, owner, CancellationToken.None);
        changed!("src");
        files.Setup(x => x.Watch(workspace, "docs", It.IsAny<Action<string>>())).Returns(second.Object);
        await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.WatchFiles, WorkspaceId = workspace.Id, Paths = ["docs"] }, owner, CancellationToken.None);

        events.Verify(x => x.Publish(new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = workspace.Id, Path = "src" }), Times.Once);
        first.Verify(x => x.Dispose(), Times.Once);
        handler.Disconnect(owner);
        second.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Handle_GetUsage_AnswersWithTheUsageOfTheService()
    {
        UsageStatus status = new() { Service = AiService.Codex, Session = new UsagePeriod { PercentUsed = 12, WindowMinutes = 300 } };
        usage.Setup(x => x.Get(AiService.Codex, It.IsAny<CancellationToken>())).ReturnsAsync(status);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GetUsage, Service = AiService.Codex }, owner, CancellationToken.None);

        Assert.Equal(status, response.Usage);
        Assert.Equal(3, response.Id);
    }

    [Fact]
    public async Task Handle_GetUsage_NeedsAService()
    {
        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GetUsage }, owner, CancellationToken.None);

        Assert.Equal("The request needs a service.", response.Error);
    }

    [Fact]
    public async Task Handle_ConfigureWorkspace_PassesModelAndEffort()
    {
        workspaces.Setup(x => x.Configure(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.Max, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Model = AiModel.Opus, Effort = AiEffort.Max });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.ConfigureWorkspace, WorkspaceId = workspace.Id, Service = AiService.ClaudeCode, Model = AiModel.Opus, Effort = AiEffort.Max }, owner, CancellationToken.None);

        Assert.Equal(AiModel.Opus, response.Workspace?.Model);
        prompts.Verify(x => x.Release(workspace.Id), Times.AtLeastOnce);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.ConfigureWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_MoveWorkspace_PassesGroupOrNone()
    {
        Guid group = Guid.NewGuid();
        workspaces.Setup(x => x.Move(workspace.Id, group, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { GroupId = group });
        workspaces.Setup(x => x.Move(workspace.Id, null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        Assert.Equal(group, (await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.MoveWorkspace, WorkspaceId = workspace.Id, GroupId = group }, owner, CancellationToken.None)).Workspace?.GroupId);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.MoveWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Workspace?.GroupId);
    }

    [Fact]
    public async Task Handle_GroupCommands_UseManager()
    {
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        Guid parent = Guid.NewGuid();
        workspaces.Setup(x => x.CreateGroup("Work", parent, It.IsAny<CancellationToken>())).ReturnsAsync(group with { ParentId = parent });
        workspaces.Setup(x => x.MoveGroup(group.Id, null, It.IsAny<CancellationToken>())).ReturnsAsync(group);
        workspaces.Setup(x => x.RenameGroup(group.Id, "Play", It.IsAny<CancellationToken>())).ReturnsAsync(group with { Name = "Play" });

        Assert.Equal(parent, (await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateGroup, Name = "Work", ParentId = parent }, owner, CancellationToken.None)).Group?.ParentId);
        Assert.Equal(group, (await handler.Handle(new RemoteRequest { Id = 5, Command = RemoteCommand.MoveGroup, GroupId = group.Id }, owner, CancellationToken.None)).Group);
        Assert.Equal("Play", (await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.RenameGroup, GroupId = group.Id, Name = "Play" }, owner, CancellationToken.None)).Group?.Name);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.DeleteGroup, GroupId = group.Id }, owner, CancellationToken.None)).Error);
        workspaces.Verify(x => x.DeleteGroup(group.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 4, Command = RemoteCommand.DeleteGroup }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_ListFoldersAndCreateFolder_UseBrowser()
    {
        FolderListing listing = new() { Path = "/a", Folders = [] };
        folders.Setup(x => x.List(null, It.IsAny<CancellationToken>())).ReturnsAsync(listing);
        folders.Setup(x => x.Create("/a", "b", It.IsAny<CancellationToken>())).ReturnsAsync(listing);

        Assert.Equal(listing, (await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.ListFolders }, owner, CancellationToken.None)).Listing);
        Assert.Equal(listing, (await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.CreateFolder, Path = "/a", Name = "b" }, owner, CancellationToken.None)).Listing);
    }

    [Fact]
    public async Task Handle_GitReads_UseTheGitServiceInTheWorkspaceFolder()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        GitStatus status = new() { IsRepo = true, Branch = "main" };
        GitChange change = new() { Path = "a.txt", Status = GitChangeStatus.Added };
        GitHistory history = new() { Commits = [] };
        GitDiff diff = new() { Path = "a.txt", Lines = [] };
        git.Setup(x => x.Status(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        git.Setup(x => x.Changes(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync([change]);
        git.Setup(x => x.History(workspace.Folder, 5, 30, It.IsAny<CancellationToken>())).ReturnsAsync(history);
        git.Setup(x => x.CommitChanges(workspace.Folder, "abc1234", It.IsAny<CancellationToken>())).ReturnsAsync([change]);
        git.Setup(x => x.Diff(workspace.Folder, "a.txt", "abc1234", It.IsAny<CancellationToken>())).ReturnsAsync(diff);

        Assert.Equal(status, (await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetGitStatus, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Git);
        Assert.Equal([change], (await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.GetGitChanges, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).GitChanges?.Changes);
        Assert.Equal(history, (await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GetGitHistory, WorkspaceId = workspace.Id, Skip = 5 }, owner, CancellationToken.None)).GitHistory);
        Assert.Equal([change], (await handler.Handle(new RemoteRequest { Id = 4, Command = RemoteCommand.GetGitCommitChanges, WorkspaceId = workspace.Id, Commit = "abc1234" }, owner, CancellationToken.None)).GitChanges?.Changes);
        Assert.Equal(diff, (await handler.Handle(new RemoteRequest { Id = 5, Command = RemoteCommand.GetGitDiff, WorkspaceId = workspace.Id, Path = "a.txt", Commit = "abc1234" }, owner, CancellationToken.None)).GitDiff);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 6, Command = RemoteCommand.GetGitCommitChanges, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_GitActions_UseTheGitServiceAndReportItsFailures()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        git.Setup(x => x.CreateBranch(workspace.Folder, "taken", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("exists"));

        Assert.Null((await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GitCommit, WorkspaceId = workspace.Id, Text = " message " }, owner, CancellationToken.None)).Error);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.GitReset, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GitCreateTag, WorkspaceId = workspace.Id, Name = "v1" }, owner, CancellationToken.None)).Error);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 4, Command = RemoteCommand.GitSetRemote, WorkspaceId = workspace.Id, Url = " https://x/y.git " }, owner, CancellationToken.None)).Error);
        Assert.Equal("exists", (await handler.Handle(new RemoteRequest { Id = 5, Command = RemoteCommand.GitCreateBranch, WorkspaceId = workspace.Id, Name = "taken" }, owner, CancellationToken.None)).Error);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 6, Command = RemoteCommand.GitCommit, WorkspaceId = workspace.Id, Text = "  " }, owner, CancellationToken.None)).Error);
        git.Verify(x => x.Commit(workspace.Folder, "message", It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(x => x.Reset(workspace.Folder, It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(x => x.CreateTag(workspace.Folder, "v1", It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(x => x.SetRemote(workspace.Folder, "https://x/y.git", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_GitActionsThatChangeTheWorkingTree_RefuseWhileTheAiWorks()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        foreach (RemoteCommand command in new[] { RemoteCommand.GitCommit, RemoteCommand.GitReset, RemoteCommand.GitCreateBranch })
        {
            RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = command, WorkspaceId = workspace.Id, Text = "m", Name = "n" }, owner, CancellationToken.None);

            Assert.Contains("AI is working", response.Error);
        }

        git.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_CloneWorkspace_ClonesThenCreatesTheWorkspace()
    {
        git.Setup(x => x.Clone("/work", "https://x/app.git", "app", It.IsAny<CancellationToken>())).ReturnsAsync("/work/app");
        workspaces.Setup(x => x.Create("/work/app", It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CloneWorkspace, Path = "/work", Url = "https://x/app.git", Name = "app" }, owner, CancellationToken.None);

        Assert.Equal(workspace, response.Workspace);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.CloneWorkspace, Path = "/work" }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_VersioningCommands_UseTheVersioningServiceWithTheWorkspace()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        GitBranchState state = new() { Unique = 2, IsBased = true, Subject = "tip" };
        versioning.Setup(x => x.BranchState(workspace, "feature", "main", It.IsAny<CancellationToken>())).ReturnsAsync(state);

        RemoteResponse branch = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetGitBranchState, WorkspaceId = workspace.Id, Name = "feature", Base = "main" }, owner, CancellationToken.None);
        foreach (RemoteRequest request in new RemoteRequest[]
        {
            new() { Id = 2, Command = RemoteCommand.GitCheckout, WorkspaceId = workspace.Id, Name = " feature ", Force = true },
            new() { Id = 3, Command = RemoteCommand.GitDeleteBranch, WorkspaceId = workspace.Id, Name = "feature" },
            new() { Id = 4, Command = RemoteCommand.GitDeleteTag, WorkspaceId = workspace.Id, Name = "v1" },
            new() { Id = 5, Command = RemoteCommand.GitSquash, WorkspaceId = workspace.Id, Name = "feature", Base = "main", Text = "msg" },
            new() { Id = 6, Command = RemoteCommand.GitRebase, WorkspaceId = workspace.Id, Name = "feature", Base = "main", Text = " squash " },
            new() { Id = 7, Command = RemoteCommand.GitRebase, WorkspaceId = workspace.Id, Name = "feature", Base = "main" },
            new() { Id = 8, Command = RemoteCommand.GitMerge, WorkspaceId = workspace.Id, Name = "feature", Base = "main", Text = " " },
            new() { Id = 9, Command = RemoteCommand.GitFetch, WorkspaceId = workspace.Id },
            new() { Id = 10, Command = RemoteCommand.GitSync, WorkspaceId = workspace.Id },
            new() { Id = 11, Command = RemoteCommand.GitCancel, WorkspaceId = workspace.Id },
        })
        {
            Assert.Null((await handler.Handle(request, owner, CancellationToken.None)).Error);
        }

        Assert.Equal(state, branch.GitBranch);
        versioning.Verify(x => x.Checkout(workspace, "feature", true, It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.DeleteBranch(workspace, "feature", It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.DeleteTag(workspace, "v1", It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Squash(workspace, "feature", "main", "msg", It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Rebase(workspace, "feature", "main", "squash", It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Rebase(workspace, "feature", "main", null, It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Merge(workspace, "feature", "main", null, It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Fetch(workspace, It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Sync(workspace, It.IsAny<CancellationToken>()), Times.Once);
        versioning.Verify(x => x.Cancel(workspace.Id), Times.Once);
    }

    [Fact]
    public async Task Handle_VersioningCommandsThatChangeTheWorkingTree_RefuseWhileTheAiWorks()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        foreach (RemoteCommand command in new[] { RemoteCommand.GitCheckout, RemoteCommand.GitDeleteBranch, RemoteCommand.GitSquash, RemoteCommand.GitRebase, RemoteCommand.GitMerge })
        {
            RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = command, WorkspaceId = workspace.Id, Name = "a", Base = "b", Text = "m" }, owner, CancellationToken.None);

            Assert.Contains("AI is working", response.Error);
        }

        versioning.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Handle_GitStatus_ReportsWhetherAGitActionRuns_AndGitSetIdentityPassesTheNameAndEmail()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        git.Setup(x => x.Status(workspace.Folder, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true });
        versioning.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        RemoteResponse status = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetGitStatus, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        RemoteResponse identity = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.GitSetIdentity, WorkspaceId = workspace.Id, Name = "Ann", Email = "a@b.c" }, owner, CancellationToken.None);

        Assert.True(status.Git?.Busy);
        Assert.Null(identity.Error);
        git.Verify(x => x.SetIdentity(workspace.Folder, "Ann", "a@b.c", It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GitSetIdentity, WorkspaceId = workspace.Id, Name = "Ann" }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_Prompt_IsRefusedWhileAScriptRuns()
    {
        scripts.Setup(x => x.IsRunning(workspace.Id)).Returns(true);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "go" }, owner, CancellationToken.None);

        Assert.Equal("A script is running in this workspace right now.", response.Error);
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RunScript_StartsTheScriptInTheWorkspace()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        scripts.Verify(x => x.Run(workspace, "a.cs", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RunScript_IsRefusedWhileTheAiWorksOrGitActs()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(true);

        RemoteResponse working = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        prompts.Setup(x => x.IsBusy(workspace.Id)).Returns(false);
        versioning.Setup(x => x.IsBusy(workspace.Id)).Returns(true);
        RemoteResponse acting = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        Assert.Equal("The AI is working in this workspace right now.", working.Error);
        Assert.Equal("A git action is running in this workspace right now.", acting.Error);
        scripts.Verify(x => x.Run(It.IsAny<WorkspaceInfo>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RenameAndDelete_AreRefusedWhileAScriptRuns()
    {
        scripts.Setup(x => x.IsRunning(workspace.Id)).Returns(true);

        RemoteResponse rename = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.RenameWorkspace, WorkspaceId = workspace.Id, Name = "x" }, owner, CancellationToken.None);
        RemoteResponse delete = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal("A script is running in this workspace right now.", rename.Error);
        Assert.Equal("A script is running in this workspace right now.", delete.Error);
    }

    [Fact]
    public async Task Handle_ScriptCommands_ReachTheRunner()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        ScriptRuns runs = new() { Runs = [] };
        scripts.Setup(x => x.List(workspace.Id)).Returns(runs);

        RemoteResponse list = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetScripts, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.StopScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);
        await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.SendScriptInput, WorkspaceId = workspace.Id, Path = "a.cs", Text = "yes" }, owner, CancellationToken.None);
        await handler.Handle(new RemoteRequest { Id = 4, Command = RemoteCommand.RemoveScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        Assert.Equal(runs, list.Scripts);
        scripts.Verify(x => x.Stop(workspace.Id, "a.cs"), Times.Once);
        scripts.Verify(x => x.SendInput(workspace.Id, "a.cs", "yes", It.IsAny<CancellationToken>()), Times.Once);
        scripts.Verify(x => x.Remove(workspace.Id, "a.cs"), Times.Once);
    }
}
