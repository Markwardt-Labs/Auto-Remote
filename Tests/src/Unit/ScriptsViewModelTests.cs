namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ScriptsViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private ScriptRun Run(string path, ScriptState state, string? task = null, bool isTask = false, string output = "", params string[] scripts) => new() { Path = path, Name = Path.GetFileNameWithoutExtension(path), State = state, IsTask = isTask, TaskPath = task, Scripts = scripts, Output = output };

    private RemoteEvent Changed(ScriptRun run) => new() { Kind = RemoteEventKind.ScriptChanged, WorkspaceId = workspace.Id, Script = run };

    private RemoteEvent Output(string path, string text) => new() { Kind = RemoteEventKind.ScriptOutput, WorkspaceId = workspace.Id, Path = path, Text = text };

    private ScriptsViewModel Create(params ScriptRun[] runs)
    {
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = runs });
        return new ScriptsViewModel(connection.Object, workspace.Id);
    }

    [Fact]
    public void AutoScroll_IsOnUntilTurnedOff()
    {
        ScriptsViewModel scripts = Create();
        Assert.True(scripts.AutoScroll);

        scripts.AutoScroll = false;

        Assert.False(scripts.AutoScroll);
    }

    [Fact]
    public async Task Start_ShowsTheHeldRunsWithTheirScriptsUnderTheirTask()
    {
        ScriptsViewModel scripts = Create(Run("build.task", ScriptState.Running, isTask: true, scripts: ["a.cs", "b.cs"]), Run("a.cs", ScriptState.Running, "build.task"), Run("b.cs", ScriptState.Pending, "build.task"), Run("solo.cs", ScriptState.Succeeded, output: "done"));

        await scripts.Start();

        Assert.Equal(["build.task", "solo.cs"], scripts.Entries.Select(entry => entry.Path));
        Assert.Equal(["a.cs", "b.cs"], scripts.Entries[0].Children.Select(entry => entry.Path));
        Assert.True(scripts.IsRunning);
        Assert.Equal(["a.cs", "b.cs", "build.task"], scripts.ActivePaths.Order());
        scripts.SelectedEntry = scripts.Entries[1];
        Assert.Equal("done", scripts.OutputText);
        Assert.True(scripts.ShowSucceeded);
        Assert.False(scripts.ShowStop);
    }

    [Fact]
    public async Task Run_SelectsTheRunWhenTheServerStartsIt()
    {
        ScriptsViewModel scripts = Create();
        await scripts.Start();

        Assert.True(await scripts.Run("a.cs"));
        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Running)));
        connection.Raise(x => x.EventReceived += null, Output("a.cs", "hello\n"));
        connection.Raise(x => x.EventReceived += null, Output("a.cs", "world\n"));

        Assert.Same(scripts.Entries[0], scripts.SelectedEntry);
        Assert.Equal("hello\nworld\n", scripts.OutputText);
        Assert.True(scripts.IsRunning);
        Assert.True(scripts.ShowRunning);
        Assert.True(scripts.ShowStop);
        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Failed)));
        Assert.False(scripts.IsRunning);
        Assert.True(scripts.ShowFailed);
    }

    [Fact]
    public async Task Run_ShowsTheReasonWhenTheServerRefuses()
    {
        ScriptsViewModel scripts = Create();
        connection.Setup(x => x.RunScript(workspace.Id, "a.cs", It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("The AI is working in this workspace right now."));
        await scripts.Start();

        Assert.False(await scripts.Run("a.cs"));

        Assert.Equal("The AI is working in this workspace right now.", scripts.Error);
    }

    [Fact]
    public async Task Events_ThatArriveWhileTheRunsLoadAreKept()
    {
        TaskCompletionSource<ScriptRuns> loading = new();
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).Returns(loading.Task);
        ScriptsViewModel scripts = new(connection.Object, workspace.Id);

        Task start = scripts.Start();
        connection.Raise(x => x.EventReceived += null, Output("a.cs", "later"));
        loading.SetResult(new ScriptRuns { Runs = [Run("a.cs", ScriptState.Running, output: "first ")] });
        await start;
        scripts.SelectedEntry = scripts.Entries[0];

        Assert.Equal("first later", scripts.OutputText);
    }

    [Fact]
    public async Task Task_ShowsOneScriptAtATimeAndFollowsTheRunningOne()
    {
        ScriptsViewModel scripts = Create(Run("t.task", ScriptState.Running, isTask: true, scripts: ["a.cs", "b.cs"]), Run("a.cs", ScriptState.Running, "t.task", output: "A"), Run("b.cs", ScriptState.Pending, "t.task", output: string.Empty));
        await scripts.Start();
        scripts.SelectedEntry = scripts.Entries[0];

        Assert.True(scripts.IsTaskSelected);
        Assert.Equal("A", scripts.OutputText);

        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Succeeded, "t.task")));
        connection.Raise(x => x.EventReceived += null, Changed(Run("b.cs", ScriptState.Running, "t.task")));
        connection.Raise(x => x.EventReceived += null, Output("b.cs", "B"));

        Assert.Equal("b", scripts.ScriptName);
        Assert.Equal("B", scripts.OutputText);
        scripts.SelectChildCommand.Execute(scripts.Entries[0].Children[0]);
        Assert.Equal("a", scripts.ScriptName);
        Assert.True(scripts.Entries[0].Children[0].IsSelected);
        Assert.True(scripts.ShowStop);
    }

    [Fact]
    public async Task Removed_DropsTheRunAndClearsTheView()
    {
        ScriptsViewModel scripts = Create(Run("a.cs", ScriptState.Succeeded));
        await scripts.Start();
        scripts.SelectedEntry = scripts.Entries[0];

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.ScriptRemoved, WorkspaceId = workspace.Id, Path = "a.cs" });

        Assert.Empty(scripts.Entries);
        Assert.False(scripts.HasScript);
        Assert.False(scripts.HasEntries);
    }

    [Fact]
    public async Task SendInput_WritesTheLineToTheRunningScript()
    {
        ScriptsViewModel scripts = Create(Run("a.cs", ScriptState.Running));
        await scripts.Start();
        scripts.SelectedEntry = scripts.Entries[0];
        scripts.InputText = "yes";

        scripts.SendInputCommand.Execute(null);
        await Task.Yield();

        connection.Verify(x => x.SendScriptInput(workspace.Id, "a.cs", "yes", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(string.Empty, scripts.InputText);
    }

    [Fact]
    public async Task Output_IsCutAtTheStartWhenItGrowsTooLong()
    {
        ScriptsViewModel scripts = Create(Run("a.cs", ScriptState.Running));
        await scripts.Start();
        scripts.SelectedEntry = scripts.Entries[0];

        connection.Raise(x => x.EventReceived += null, Output("a.cs", new string('x', 600_000) + "END"));

        Assert.Equal(500_000, scripts.Entries[0].Output.Length);
        Assert.EndsWith("END", scripts.OutputText);
        Assert.StartsWith("...", scripts.OutputText);
    }

    [Fact]
    public async Task Workspace_BlocksThePromptAndShowsWorkingWhileGitWorks()
    {
        GitStatus status = new() { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "second" };
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await model.Git.Refresh();
        model.Chat.Input = "hello";

        Assert.False(model.IsWorking);
        Assert.True(model.Chat.SendCommand.CanExecute(null));

        status = status with { Busy = true };
        await model.Git.Refresh();

        Assert.True(model.IsWorking);
        Assert.Equal(WorkspaceState.Managing, model.State);
        Assert.False(model.IsAiWorking);
        Assert.False(model.Chat.SendCommand.CanExecute(null));
        Assert.Equal("Git is working, wait for it to finish", model.Chat.Placeholder);

        status = status with { Busy = false };
        await model.Git.Refresh();

        Assert.False(model.IsWorking);
        Assert.Equal(WorkspaceState.Ready, model.State);
        Assert.True(model.Chat.SendCommand.CanExecute(null));
        Assert.Equal("Enter query...", model.Chat.Placeholder);
    }

    [Fact]
    public async Task Workspace_WhileTheAiWorksTakesMoreAiInputButNoGitActionsOrScripts()
    {
        GitStatus status = new() { IsRepo = true, Branch = "main", Commit = "abc1234", Message = "second" };
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await model.Git.Refresh();
        model.Chat.Input = "hello";
        Assert.True(model.Git.CommitCommand.CanExecute(null));

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });

        Assert.Equal(WorkspaceState.Working, model.State);
        Assert.True(model.Chat.SendCommand.CanExecute(null));
        Assert.False(model.Git.CommitCommand.CanExecute(null));
        Assert.False(model.Git.FetchCommand.CanExecute(null));
        await model.Git.Checkout("feature");
        Assert.Equal(GitPrompt.Notice, model.Git.Prompt);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = false });

        Assert.Equal(WorkspaceState.Ready, model.State);
        Assert.True(model.Git.CommitCommand.CanExecute(null));
    }

    [Fact]
    public async Task Workspace_BlocksThePromptWhileAScriptRunsAndMarksItsFile()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }, new FileEntry { Name = "b.txt", Path = "b.txt", IsFolder = false }] });
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await model.Files.Start();
        model.Chat.Input = "hello";

        Assert.True(model.Chat.SendCommand.CanExecute(null));
        Assert.Equal([true, false], model.Files.Nodes.Select(node => node.IsRunnable));

        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Running)));

        Assert.True(model.IsWorking);
        Assert.False(model.IsAiWorking);
        Assert.False(model.Chat.SendCommand.CanExecute(null));
        Assert.Equal("A script is running, wait for it to finish", model.Chat.Placeholder);
        Assert.True(model.Files.Nodes[0].IsScriptRunning);

        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Succeeded)));

        Assert.False(model.IsWorking);
        Assert.True(model.Chat.SendCommand.CanExecute(null));
        Assert.False(model.Files.Nodes[0].IsScriptRunning);
    }

    [Fact]
    public async Task Workspace_RunsADoubleTappedScriptAndRefusesWhileTheAiWorks()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0, Working = true });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }] });
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await model.Files.Start();

        model.Files.Run(model.Files.Nodes[0]);
        await Task.Delay(50);

        connection.Verify(x => x.RunScript(workspace.Id, "a.cs", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal("Wait for the AI to finish before running a script.", model.Scripts.Error);
        Assert.True(model.ShowScriptView);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = false });
        model.Files.Run(model.Files.Nodes[0]);
        await Task.Delay(50);

        connection.Verify(x => x.RunScript(workspace.Id, "a.cs", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Workspace_WithTabsRunsScriptsAndShowsThemInTheirOwnTab()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [new FileEntry { Name = "a.cs", Path = "a.cs", IsFolder = false }, new FileEntry { Name = "b.task", Path = "b.task", IsFolder = false }, new FileEntry { Name = "c.txt", Path = "c.txt", IsFolder = false }] });
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();
        await model.Files.Start();

        Assert.Equal([true, true, false], model.Files.Nodes.Select(node => node.IsRunnable));
        Assert.False(model.ShowSideTabs);
        Assert.False(model.ShowScriptView);

        model.Files.Run(model.Files.Nodes[0]);
        await Task.Delay(50);

        connection.Verify(x => x.RunScript(workspace.Id, "a.cs", It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(model.ShowScriptView);
        Assert.True(model.ShowChatPanel);
        Assert.False(model.ShowChatView);
        Assert.False(model.ShowFilesPanel);

        model.ShowChatCommand.Execute(null);

        Assert.False(model.ShowScriptView);
        Assert.True(model.ShowChatView);
    }

    private async Task<WorkspaceViewModel> StartedWorkspace(bool repo)
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = repo, Branch = "main", Commit = "abc1234", Message = "second" });
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Ready);
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await model.Git.Refresh();
        return model;
    }

    private void Server(WorkspaceState state) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.StateChanged, WorkspaceId = workspace.Id, State = state });

    [Fact]
    public async Task Workspace_ShowsStoppingAndCancelingFromTheServerAndLocksEverythingMeanwhile()
    {
        WorkspaceViewModel model = await StartedWorkspace(true);
        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = true });
        Assert.True(model.CanHalt);
        Assert.True(model.CanCancel);

        Server(WorkspaceState.Stopping);

        Assert.Equal(WorkspaceState.Stopping, model.State);
        Assert.True(model.IsWorking);
        Assert.False(model.CanHalt);
        Assert.False(model.CanCancel);
        Assert.False(model.Chat.SendCommand.CanExecute(null) && !string.IsNullOrEmpty(model.Chat.Input));
        Assert.Equal("Stopping, wait for it to finish", model.Chat.Placeholder);
        Assert.False(model.Git.CommitCommand.CanExecute(null));

        Server(WorkspaceState.Canceling);
        Assert.Equal(WorkspaceState.Canceling, model.State);
        Assert.Equal("Cancelling and reverting, wait for it to finish", model.Chat.Placeholder);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = false });
        Server(WorkspaceState.Ready);

        Assert.Equal(WorkspaceState.Ready, model.State);
        Assert.False(model.IsWorking);
        Assert.Null(model.Chat.BlockReason);
    }

    [Fact]
    public async Task Workspace_ReadsAStateThatWasAlreadyStoppingWhenItOpened()
    {
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Canceling);
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);

        await model.Start();
        await Task.Delay(50);

        Assert.Equal(WorkspaceState.Canceling, model.State);
    }

    [Fact]
    public async Task Workspace_CanOnlyCancelWorkThatCanBeRevertedButAlwaysStopIt()
    {
        WorkspaceViewModel plain = await StartedWorkspace(false);
        Assert.False(plain.CanHalt);
        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Running)));

        Assert.True(plain.CanHalt);
        Assert.False(plain.CanCancel);

        WorkspaceViewModel repo = await StartedWorkspace(true);
        connection.Raise(x => x.EventReceived += null, Changed(Run("a.cs", ScriptState.Running)));

        Assert.True(repo.CanCancel);
    }

    [Fact]
    public async Task Workspace_StopAndCancelAskTheServerAndShowARefusalInTheChat()
    {
        WorkspaceViewModel model = await StartedWorkspace(true);
        connection.Setup(x => x.CancelWorkspace(workspace.Id, It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("Nothing is running in this workspace."));

        await model.Stop();
        await model.Cancel();

        connection.Verify(x => x.StopWorkspace(workspace.Id, It.IsAny<CancellationToken>()), Times.Once);
        connection.Verify(x => x.CancelWorkspace(workspace.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal("Nothing is running in this workspace.", model.Chat.Error);
    }
}
