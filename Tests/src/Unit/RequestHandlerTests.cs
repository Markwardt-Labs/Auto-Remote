namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class RequestHandlerTests
{
    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IFolderBrowser> folders = new();

    private readonly Mock<IWorkspaceFileBrowser> files = new();

    private readonly Mock<IGitService> git = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<ISavedInputs> savedInputs = new();

    private readonly Mock<IScriptRunner> scripts = new();

    private readonly Mock<IUsageService> usage = new();

    private readonly Mock<IEventHub> events = new();

    private readonly Mock<IWorkspaceWatcher> watcher = new();

    private readonly WorkspaceActivity activity;

    private readonly Mock<IWorkspaceControl> control = new();

    private readonly Mock<ISpeechService> speech = new();

    private readonly Mock<IPrompterHost> host = new();

    private readonly Mock<IWorkspaceLoops> loops = new();

    private readonly Mock<IServerUpdater> updater = new();

    private readonly object owner = new();

    private readonly RequestHandler handler;

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    public RequestHandlerTests()
    {
        activity = new WorkspaceActivity(events.Object);
        versioning.Setup(x => x.Perform(It.IsAny<WorkspaceInfo>(), It.IsAny<bool>(), It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>(), It.IsAny<bool>())).Returns((WorkspaceInfo _, bool _, Func<CancellationToken, Task> action, CancellationToken token, bool _) => action(token));
        handler = new RequestHandler(workspaces.Object, folders.Object, files.Object, git.Object, versioning.Object, savedInputs.Object, prompts.Object, scripts.Object, usage.Object, activity, control.Object, speech.Object, host.Object, loops.Object, watcher.Object, updater.Object);
    }

    [Fact]
    public async Task Open_ConfiguresPromptsAndOpensWorkspaces()
    {
        ServerConfig config = new() { Port = 1, Key = "k", DataFolder = "d" };

        await handler.Open(config, CancellationToken.None);

        host.Verify(x => x.Configure(config, CancellationToken.None), Times.Once);
        prompts.Verify(x => x.Configure(CancellationToken.None), Times.Once);
        speech.Verify(x => x.Configure(config), Times.Once);
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
        workspaces.Setup(x => x.Create("/work/app", AiService.ClaudeCode, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

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
        workspaces.Setup(x => x.Create(It.IsAny<string>(), It.IsAny<AiService>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("taken"));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/x" }, owner, CancellationToken.None);

        Assert.Equal("taken", response.Error);
    }

    [Theory]
    [InlineData(RemoteCommand.RenameWorkspace)]
    [InlineData(RemoteCommand.MoveWorkspaceToFolder)]
    [InlineData(RemoteCommand.DeleteWorkspace)]
    public async Task Handle_RenameMoveOrDeleteWhileNotReady_IsRefused(RemoteCommand command)
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = command, WorkspaceId = workspace.Id, Name = "n" }, owner, CancellationToken.None);

        Assert.NotNull(response.Error);
        workspaces.Verify(x => x.Delete(It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        workspaces.Verify(x => x.Rename(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        workspaces.Verify(x => x.MoveToFolder(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_MoveWorkspaceToFolder_ClosesTheSessionStopsWatchingAndMovesTheWorkspaceToThePath()
    {
        WorkspaceInfo moved = workspace with { Folder = "/archive/app" };
        workspaces.Setup(x => x.MoveToFolder(workspace.Id, "/archive", It.IsAny<CancellationToken>())).ReturnsAsync(moved);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.MoveWorkspaceToFolder, WorkspaceId = workspace.Id, Path = "/archive" }, owner, CancellationToken.None);
        RemoteResponse missing = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.MoveWorkspaceToFolder, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal(moved, response.Workspace);
        Assert.Contains("needs a path", missing.Error);
        prompts.Verify(x => x.Release(workspace.Id), Times.AtLeastOnce);
        watcher.Verify(x => x.Forget(workspace.Id), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Handle_MoveWorkspaceToFolder_ReportsWhyTheWorkspaceCannotMove()
    {
        workspaces.Setup(x => x.MoveToFolder(workspace.Id, "/archive", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("'app' already exists in '/archive'."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.MoveWorkspaceToFolder, WorkspaceId = workspace.Id, Path = "/archive" }, owner, CancellationToken.None);

        Assert.Contains("already exists", response.Error);
    }

    [Fact]
    public async Task Handle_DeleteWorkspace_PassesFolderChoiceAndForgetsTheAiSession()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = workspace.Id, DeleteFolder = false }, owner, CancellationToken.None);
        await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = workspace.Id, DeleteFolder = true }, owner, CancellationToken.None);

        workspaces.Verify(x => x.Delete(workspace.Id, false, It.IsAny<CancellationToken>()), Times.Once);
        workspaces.Verify(x => x.Delete(workspace.Id, true, It.IsAny<CancellationToken>()), Times.Once);
        prompts.Verify(x => x.Forget(workspace, false), Times.Once);
        prompts.Verify(x => x.Forget(workspace, true), Times.Once);
    }

    [Fact]
    public async Task Handle_DeleteWorkspace_KeepsTheAiSessionWhenTheDeleteIsRefused()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        workspaces.Setup(x => x.Delete(workspace.Id, true, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("in use"));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.DeleteWorkspace, WorkspaceId = workspace.Id, DeleteFolder = true }, owner, CancellationToken.None);

        Assert.Equal("in use", response.Error);
        prompts.Verify(x => x.Forget(It.IsAny<WorkspaceInfo>(), It.IsAny<bool>()), Times.Never);
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
    public async Task Handle_UpdateServer_UpdatesTheServerAndAnswers()
    {
        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 5, Command = RemoteCommand.UpdateServer }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(5, response.Id);
        updater.Verify(x => x.Update(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(typeof(RequestException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task Handle_UpdateServer_ReportsWhyTheUpdateFailed(Type failure)
    {
        updater.Setup(x => x.Update(It.IsAny<CancellationToken>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, "no way")!);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 6, Command = RemoteCommand.UpdateServer }, owner, CancellationToken.None);

        Assert.Equal("no way", response.Error);
    }

    [Fact]
    public async Task Handle_CreateWorkspace_FilesItInTheGivenGroup()
    {
        Guid group = Guid.NewGuid();
        workspaces.Setup(x => x.Create("/work/app", AiService.ClaudeCode, null, group, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { GroupId = group });
        host.SetupGet(x => x.DefaultService).Returns(AiService.ClaudeCode);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/work/app", GroupId = group }, owner, CancellationToken.None);

        Assert.Equal(group, response.Workspace?.GroupId);
    }

    [Fact]
    public async Task Handle_SetAiEnvironment_GivesOpenSessionsTheirInstructionsAgain()
    {
        host.Setup(x => x.Set(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AiEnvironment { StatusMinutes = 9 });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.SetAiEnvironment, AiEnvironment = new AiEnvironment { StatusMinutes = 9 } }, owner, CancellationToken.None);

        Assert.Equal(9, response.AiEnvironment?.StatusMinutes);
        prompts.Verify(x => x.RefreshInstructions(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_Transcribe_AnswersTheTextOfTheSpeech()
    {
        speech.Setup(x => x.Transcribe("AQ==", It.IsAny<CancellationToken>())).ReturnsAsync("hello there");

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Transcribe, Data = "AQ==" }, owner, CancellationToken.None);

        Assert.Equal("hello there", response.Transcript);
    }

    [Fact]
    public async Task Handle_Transcribe_ReportsAFailureOrMissingData()
    {
        speech.Setup(x => x.Transcribe("AQ==", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("not set up"));

        RemoteResponse failed = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Transcribe, Data = "AQ==" }, owner, CancellationToken.None);
        RemoteResponse missing = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.Transcribe }, owner, CancellationToken.None);

        Assert.Equal("not set up", failed.Error);
        Assert.NotNull(missing.Error);
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
        ChatHistory chunk = new() { Messages = [], Older = "4:0", Working = true };
        prompts.Setup(x => x.History(workspace.Id, "9:2", 5, It.IsAny<CancellationToken>())).ReturnsAsync(chunk);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetHistory, WorkspaceId = workspace.Id, Before = "9:2", Limit = 5 }, owner, CancellationToken.None);

        Assert.Equal(chunk, response.History);
    }

    [Fact]
    public async Task Handle_WatchWorkspace_JoinsTheWatchOfTheOpenWorkspaceAndLeavesTheOneBeforeOnlyAfterwards()
    {
        Mock<IDisposable> first = new();
        Mock<IDisposable> second = new();
        WorkspaceInfo other = workspace with { Id = Guid.NewGuid(), Folder = "/work/other" };
        List<string> order = [];
        first.Setup(x => x.Dispose()).Callback(() => order.Add("leave first"));
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        workspaces.Setup(x => x.Get(other.Id, It.IsAny<CancellationToken>())).ReturnsAsync(other);
        watcher.Setup(x => x.Watch(workspace)).Returns(first.Object);
        watcher.Setup(x => x.Watch(other)).Returns(() =>
        {
            order.Add("join second");
            return second.Object;
        });

        await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.WatchWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.WatchWorkspace, WorkspaceId = other.Id }, owner, CancellationToken.None);

        Assert.Equal(["join second", "leave first"], order);
        handler.Disconnect(owner);
        second.Verify(x => x.Dispose(), Times.Once);
        first.Verify(x => x.Dispose(), Times.Once);
    }

    [Fact]
    public async Task Handle_WatchWorkspace_RefusesAnUnknownWorkspace()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("No such workspace."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.WatchWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal("No such workspace.", response.Error);
        watcher.Verify(x => x.Watch(It.IsAny<WorkspaceInfo>()), Times.Never);
    }

    [Fact]
    public async Task Handle_GetUsage_AnswersWithTheUsageOfTheService()
    {
        UsageStatus status = new() { Service = AiService.Codex, Windows = [new UsagePeriod { Name = "Session", PercentUsed = 12, WindowMinutes = 300 }] };
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
    public async Task Handle_StartLoop_PassesTheSettingsOnWhileTheAiCanTakeInput()
    {
        LoopSettings settings = new() { Prompt = "check", Condition = LoopCondition.Runs, Runs = 3, IntervalSeconds = 60 };

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.StartLoop, WorkspaceId = workspace.Id, Loop = settings }, owner, CancellationToken.None);
        RemoteResponse missing = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.StartLoop, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        activity.TryEnter(workspace.Id, WorkspaceState.Running);
        RemoteResponse busy = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.StartLoop, WorkspaceId = workspace.Id, Loop = settings }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Contains("needs a loop", missing.Error);
        Assert.NotNull(busy.Error);
        loops.Verify(x => x.Start(workspace.Id, settings, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CancelLoopAndGetLoop_CancelTheLoopAndAnswerWithIt()
    {
        LoopInfo info = new() { Settings = new LoopSettings { Prompt = "check" }, StartedAt = DateTimeOffset.UtcNow, Runs = 2 };
        loops.Setup(x => x.Get(workspace.Id)).Returns(info);

        RemoteResponse read = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetLoop, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        RemoteResponse cancelled = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.CancelLoop, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal(info, read.Loop);
        Assert.Null(cancelled.Error);
        loops.Verify(x => x.Cancel(workspace.Id), Times.Once);
    }

    [Fact]
    public async Task Handle_CreateWorkspace_GivesTheWorkspaceTheRequestedProviderAndTheKeyForItsSession()
    {
        workspaces.Setup(x => x.Create("/work/app", AiService.Codex, "sk-1", null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Service = AiService.Codex });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/work/app", Service = AiService.Codex, ApiKey = "sk-1" }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(AiService.Codex, response.Workspace?.Service);
    }

    [Fact]
    public async Task Handle_CreateWorkspace_UsesTheDefaultProviderOfTheEnvironmentWhenNoneIsGiven()
    {
        host.Setup(x => x.DefaultService).Returns(AiService.Local);
        workspaces.Setup(x => x.Create("/work/app", AiService.Local, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Service = AiService.Local });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/work/app" }, owner, CancellationToken.None);

        Assert.Equal(AiService.Local, response.Workspace?.Service);
    }

    [Fact]
    public async Task Handle_CreateWorkspace_ReportsWhyTheWorkspaceOrItsSessionCannotBeMade()
    {
        workspaces.Setup(x => x.Create("/work/app", AiService.ClaudeCode, "bad", null, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The key was refused."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CreateWorkspace, Path = "/work/app", ApiKey = "bad" }, owner, CancellationToken.None);

        Assert.Equal("The key was refused.", response.Error);
    }

    [Fact]
    public async Task Handle_CloneWorkspace_GivesTheCloneTheRequestedProviderAndKey()
    {
        git.Setup(x => x.Clone("/work", "https://x/app.git", null, It.IsAny<CancellationToken>())).ReturnsAsync("/work/app");
        workspaces.Setup(x => x.Create("/work/app", AiService.Codex, "sk-2", null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Service = AiService.Codex });

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.CloneWorkspace, Path = "/work", Url = "https://x/app.git", Service = AiService.Codex, ApiKey = "sk-2" }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.Equal(AiService.Codex, response.Workspace?.Service);
    }

    [Fact]
    public async Task Handle_GetAndSetWorkspaceSettings_ReadAndChangeHowTheAiSessionRuns()
    {
        WorkspaceSettings settings = new() { Capability = AiCapability.Strong, Effort = AiEffort.Max, IsShellAllowed = true };
        prompts.Setup(x => x.GetSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        prompts.Setup(x => x.SetSettings(workspace.Id, settings, It.IsAny<CancellationToken>())).ReturnsAsync(settings with { Instructions = "kept" });

        RemoteResponse read = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetWorkspaceSettings, WorkspaceId = workspace.Id }, owner, CancellationToken.None);
        RemoteResponse written = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.SetWorkspaceSettings, WorkspaceId = workspace.Id, Settings = settings }, owner, CancellationToken.None);
        RemoteResponse missing = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.SetWorkspaceSettings, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal(settings, read.Settings);
        Assert.Equal("kept", written.Settings?.Instructions);
        Assert.Contains("needs settings", missing.Error);
    }

    [Fact]
    public async Task Handle_GetAndSetAiEnvironment_ReadAndChangeTheEnvironment()
    {
        AiEnvironment environment = new() { DefaultService = AiService.Codex, HasCodexApiKey = true };
        AiEnvironment given = new() { DefaultService = AiService.Codex, CodexApiKey = "sk-3" };
        host.Setup(x => x.Get()).Returns(environment);
        host.Setup(x => x.Set(given, It.IsAny<CancellationToken>())).ReturnsAsync(environment);

        RemoteResponse read = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetAiEnvironment }, owner, CancellationToken.None);
        RemoteResponse written = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.SetAiEnvironment, AiEnvironment = given }, owner, CancellationToken.None);
        RemoteResponse missing = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.SetAiEnvironment }, owner, CancellationToken.None);

        Assert.Equal(environment, read.AiEnvironment);
        Assert.Equal(environment, written.AiEnvironment);
        Assert.Contains("needs an environment", missing.Error);
    }

    [Fact]
    public async Task Handle_SetAiEnvironment_ReportsWhyTheEnvironmentWasRefused()
    {
        host.Setup(x => x.Set(It.IsAny<AiEnvironment>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("OpenAI image generation needs an API key."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.SetAiEnvironment, AiEnvironment = new AiEnvironment() }, owner, CancellationToken.None);

        Assert.Contains("needs an API key", response.Error);
    }

    [Fact]
    public async Task Handle_ListLocalModelsAndListImageModels_AskTheGivenServerOrTheEnvironmentsOwn()
    {
        host.Setup(x => x.ListLocalModels("http://other:1234/v1", It.IsAny<CancellationToken>())).ReturnsAsync(["gemma", "qwen"]);
        host.Setup(x => x.ListImageModels(null, It.IsAny<CancellationToken>())).ReturnsAsync(["sdxl"]);

        RemoteResponse models = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.ListLocalModels, Url = "http://other:1234/v1" }, owner, CancellationToken.None);
        RemoteResponse checkpoints = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.ListImageModels }, owner, CancellationToken.None);

        Assert.Equal(["gemma", "qwen"], models.Names);
        Assert.Equal(["sdxl"], checkpoints.Names);
    }

    [Fact]
    public async Task Handle_ListLocalModels_ReportsAServerThatCannotBeReached()
    {
        host.Setup(x => x.ListLocalModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("The model server could not be reached."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.ListLocalModels }, owner, CancellationToken.None);

        Assert.Contains("could not be reached", response.Error);
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
    public async Task Handle_ShiftGroup_PassesTheOffsetAndNeedsOne()
    {
        Guid group = Guid.NewGuid();

        Assert.Null((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.ShiftGroup, GroupId = group, Offset = 1 }, owner, CancellationToken.None)).Error);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.ShiftGroup, GroupId = group }, owner, CancellationToken.None)).Error);
        workspaces.Verify(x => x.ShiftGroup(group, 1, It.IsAny<CancellationToken>()), Times.Once);
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
        activity.TryEnter(workspace.Id, WorkspaceState.Working);

        foreach (RemoteCommand command in new[] { RemoteCommand.GitCommit, RemoteCommand.GitReset, RemoteCommand.GitCreateBranch, RemoteCommand.GitCreateTag, RemoteCommand.GitSetRemote })
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
        workspaces.Setup(x => x.Create("/work/app", AiService.ClaudeCode, null, null, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

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
        activity.TryEnter(workspace.Id, WorkspaceState.Working);

        foreach (RemoteCommand command in new[] { RemoteCommand.GitCheckout, RemoteCommand.GitDeleteBranch, RemoteCommand.GitDeleteTag, RemoteCommand.GitSquash, RemoteCommand.GitRebase, RemoteCommand.GitMerge, RemoteCommand.GitFetch })
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
    public async Task Handle_Prompt_IsRefusedWhileAScriptRunsOrGitActs()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Running);
        RemoteResponse running = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "go" }, owner, CancellationToken.None);
        RemoteResponse attaching = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.UploadAttachment, WorkspaceId = workspace.Id, Name = "a.txt", Data = "AAAA" }, owner, CancellationToken.None);

        activity.Leave(workspace.Id, WorkspaceState.Running);
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        RemoteResponse managing = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "go" }, owner, CancellationToken.None);

        Assert.Equal("A script is running in this workspace right now.", running.Error);
        Assert.Equal("A script is running in this workspace right now.", attaching.Error);
        Assert.Equal("A git action is running in this workspace right now.", managing.Error);
        prompts.Verify(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        prompts.Verify(x => x.Attach(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_Prompt_IsAcceptedWhenReadyOrWorking()
    {
        RemoteResponse ready = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "go" }, owner, CancellationToken.None);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        RemoteResponse working = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.Prompt, WorkspaceId = workspace.Id, Text = "more" }, owner, CancellationToken.None);

        Assert.Null(ready.Error);
        Assert.Null(working.Error);
        prompts.Verify(x => x.Prompt(workspace.Id, "more", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_StopAndCancelWorkspace_GoToTheControlAndGetStateReadsTheTracker()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        activity.TryEnter(workspace.Id, WorkspaceState.Running);

        Assert.Null((await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.StopWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
        Assert.Null((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.CancelWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
        RemoteResponse state = await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.GetWorkspaceState, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        control.Verify(x => x.Stop(workspace.Id), Times.Once);
        control.Verify(x => x.Cancel(workspace.Id), Times.Once);
        Assert.Equal(WorkspaceState.Running, state.State);
    }

    [Fact]
    public async Task Handle_StopWorkspace_ReportsARefusal()
    {
        control.Setup(x => x.Stop(workspace.Id)).ThrowsAsync(new RequestException("Nothing is running in this workspace."));

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.StopWorkspace, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        Assert.Equal("Nothing is running in this workspace.", response.Error);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.CancelWorkspace }, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_GitCancel_StopsThroughTheControlWhileManaging()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);

        await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GitCancel, WorkspaceId = workspace.Id }, owner, CancellationToken.None);

        control.Verify(x => x.Stop(workspace.Id), Times.Once);
        versioning.Verify(x => x.Cancel(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RunScript_StartsTheScriptInTheWorkspace()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);

        RemoteResponse response = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        Assert.Null(response.Error);
        scripts.Verify(x => x.Run(workspace, "a.cs", It.Is<IReadOnlyDictionary<string, string>>(environment => environment.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RunScript_PassesOnlyTheDisplayVariablesAndOnlyFromAClientOnTheServersMachine()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient local = new();
        Task<TcpClient> accepted = listener.AcceptTcpClientAsync();
        await local.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using TcpClient serverSide = await accepted;
        listener.Stop();
        Dictionary<string, string> sent = new() { ["DISPLAY"] = ":1", ["XAUTHORITY"] = "/run/user/1000/gdm/Xauthority", ["PATH"] = "/evil", ["LD_PRELOAD"] = "/evil.so", ["WAYLAND_DISPLAY"] = string.Empty };
        RemoteRequest request = new() { Id = 1, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs", Environment = sent };

        await handler.Handle(request, serverSide, CancellationToken.None);
        await handler.Handle(request with { Id = 2 }, owner, CancellationToken.None);

        scripts.Verify(x => x.Run(workspace, "a.cs", It.Is<IReadOnlyDictionary<string, string>>(environment => environment.Count == 2 && environment["DISPLAY"] == ":1" && environment["XAUTHORITY"] == "/run/user/1000/gdm/Xauthority"), It.IsAny<CancellationToken>()), Times.Once);
        scripts.Verify(x => x.Run(workspace, "a.cs", It.Is<IReadOnlyDictionary<string, string>>(environment => environment.Count == 0), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_RunScript_IsRefusedWhileTheAiWorksOrGitActs()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        activity.TryEnter(workspace.Id, WorkspaceState.Working);

        RemoteResponse working = await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        activity.Leave(workspace.Id, WorkspaceState.Working);
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        RemoteResponse acting = await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.RunScript, WorkspaceId = workspace.Id, Path = "a.cs" }, owner, CancellationToken.None);

        Assert.Equal("The AI is working in this workspace right now.", working.Error);
        Assert.Equal("A git action is running in this workspace right now.", acting.Error);
        scripts.Verify(x => x.Run(It.IsAny<WorkspaceInfo>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_RenameAndDelete_AreRefusedWhileAScriptRuns()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Running);

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

    [Fact]
    public async Task Handle_GenerateCommitMessage_AnswersWhatVersioningAsksTheAiAndRefusesWhileTheAiWorks()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        versioning.Setup(x => x.CommitMessage(workspace, It.IsAny<CancellationToken>())).ReturnsAsync("Fix the login timeout");
        RemoteRequest request = new() { Id = 1, Command = RemoteCommand.GenerateCommitMessage, WorkspaceId = workspace.Id };

        Assert.Equal("Fix the login timeout", (await handler.Handle(request, owner, CancellationToken.None)).CommitMessage);

        activity.TryEnter(workspace.Id, WorkspaceState.Working);

        Assert.Contains("AI is working", (await handler.Handle(request, owner, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Handle_SavedInputCommands_UseTheSavedInputsOfTheWorkspace()
    {
        SavedInput entry = new() { Id = Guid.NewGuid(), Text = "later", Time = DateTimeOffset.UnixEpoch };
        savedInputs.Setup(x => x.List(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([entry]);
        savedInputs.Setup(x => x.Save(workspace.Id, "later", It.IsAny<CancellationToken>())).ReturnsAsync([entry]);
        savedInputs.Setup(x => x.Save(workspace.Id, "", It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("There is nothing to save."));
        savedInputs.Setup(x => x.Take(workspace.Id, entry.Id, It.IsAny<CancellationToken>())).ReturnsAsync(entry);

        Assert.Equal([entry], (await handler.Handle(new RemoteRequest { Id = 1, Command = RemoteCommand.GetSavedInputs, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).SavedInputs);
        Assert.Equal([entry], (await handler.Handle(new RemoteRequest { Id = 2, Command = RemoteCommand.SaveInput, WorkspaceId = workspace.Id, Text = "later" }, owner, CancellationToken.None)).SavedInputs);
        Assert.Equal(entry, (await handler.Handle(new RemoteRequest { Id = 3, Command = RemoteCommand.TakeSavedInput, WorkspaceId = workspace.Id, InputId = entry.Id }, owner, CancellationToken.None)).SavedInput);
        Assert.Equal("There is nothing to save.", (await handler.Handle(new RemoteRequest { Id = 4, Command = RemoteCommand.SaveInput, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 5, Command = RemoteCommand.TakeSavedInput, WorkspaceId = workspace.Id }, owner, CancellationToken.None)).Error);
        Assert.NotNull((await handler.Handle(new RemoteRequest { Id = 6, Command = RemoteCommand.GetSavedInputs }, owner, CancellationToken.None)).Error);
    }
}
