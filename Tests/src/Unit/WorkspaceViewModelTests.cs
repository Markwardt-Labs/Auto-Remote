namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private ChatHistory History(int start, bool working, params string[] texts) => new() { Start = start, Working = working, Messages = [.. texts.Select(text => new ChatMessage { Role = MessageRole.User, Text = text })] };

    private RemoteEvent Added(int index, string text, MessageRole role = MessageRole.Assistant) => new() { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Index = index, Message = new ChatMessage { Role = role, Text = text } };

    private RemoteEvent Working(bool working) => new() { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working };

    [Fact]
    public async Task Chat_LoadsHistorySendsPromptsAndShowsMessagesAsEventsArrive()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false, "earlier"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        Assert.Single(model.Chat.Messages);
        model.Chat.Input = "  build it ";
        model.Chat.SendCommand.Execute(null);
        await Task.Delay(100);
        connection.Verify(x => x.Prompt(workspace.Id, "build it", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(string.Empty, model.Chat.Input);

        connection.Raise(x => x.EventReceived += null, Added(1, "build it", MessageRole.User));
        connection.Raise(x => x.EventReceived += null, Added(2, "built"));

        Assert.Equal(["earlier", "build it", "built"], model.Chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Chat_AcceptsPromptsWhileTheAiWorks()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, true, "do it"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        Assert.True(model.IsAiWorking);
        model.Chat.Input = "also this";
        Assert.True(model.Chat.SendCommand.CanExecute(null));
        model.Chat.SendCommand.Execute(null);
        await Task.Delay(100);

        connection.Verify(x => x.Prompt(workspace.Id, "also this", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Chat_IgnoresDuplicateAndOtherWorkspacesEventsAndReloadsOnAGap()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false, "a", "b"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        connection.Raise(x => x.EventReceived += null, Added(1, "b"));
        connection.Raise(x => x.EventReceived += null, Added(2, "c") with { WorkspaceId = Guid.NewGuid() });
        Assert.Equal(2, model.Chat.Messages.Count);

        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false, "a", "b", "c", "d"));
        connection.Raise(x => x.EventReceived += null, Added(3, "d"));
        await Task.Delay(100);

        Assert.Equal(["a", "b", "c", "d"], model.Chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Chat_KeepsEventsThatArriveWhileTheHistoryLoads()
    {
        TaskCompletionSource<ChatHistory> loading = new();
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(loading.Task);
        WorkspaceViewModel model = new(connection.Object, workspace);

        Task start = model.Start();
        connection.Raise(x => x.EventReceived += null, Added(1, "new"));
        connection.Raise(x => x.EventReceived += null, Added(0, "old"));
        loading.SetResult(History(0, false, "old"));
        await start;

        Assert.Equal(["old", "new"], model.Chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Chat_LoadsOlderChunksOnDemandUntilTheBeginning()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(4, false, "e", "f"));
        connection.Setup(x => x.GetHistory(workspace.Id, 4, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(1, false, "b", "c", "d"));
        connection.Setup(x => x.GetHistory(workspace.Id, 1, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false, "a"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        Assert.True(model.Chat.HasMore);
        Assert.True(await model.Chat.LoadOlder());
        Assert.Equal(["b", "c", "d", "e", "f"], model.Chat.Messages.Select(message => message.Text));
        Assert.True(model.Chat.HasMore);
        Assert.True(await model.Chat.LoadOlder());
        Assert.Equal(["a", "b", "c", "d", "e", "f"], model.Chat.Messages.Select(message => message.Text));
        Assert.False(model.Chat.HasMore);
        Assert.False(await model.Chat.LoadOlder());

        connection.Raise(x => x.EventReceived += null, Added(6, "g"));
        Assert.Equal("g", model.Chat.Messages[^1].Text);
    }

    [Fact]
    public async Task Chat_ShowsPromptFailureAndKeepsTheText()
    {
        connection.Setup(x => x.Prompt(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("claude missing"));
        WorkspaceViewModel model = new(connection.Object, workspace);

        model.Chat.Input = "go";
        model.Chat.SendCommand.Execute(null);
        await Task.Delay(100);

        Assert.Equal("claude missing", model.Chat.Error);
        Assert.Equal("go", model.Chat.Input);
    }

    [Fact]
    public async Task Chat_ShowsFailuresTheAiReports()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspace.Id, Error = "out of tokens" });

        Assert.Equal("out of tokens", model.Chat.Error);
    }

    [Fact]
    public async Task IsAiWorking_FollowsTheServersWorkingEvents()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        List<string?> changed = [];
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        await model.Start();
        Assert.False(model.IsAiWorking);

        connection.Raise(x => x.EventReceived += null, Working(true));
        Assert.True(model.IsAiWorking);
        Assert.Contains(nameof(WorkspaceViewModel.IsAiWorking), changed);

        connection.Raise(x => x.EventReceived += null, Working(false));
        Assert.False(model.IsAiWorking);
    }

    [Fact]
    public async Task Dispose_StopsFollowingEvents()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(0, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        model.Dispose();
        connection.Raise(x => x.EventReceived += null, Working(true));

        Assert.False(model.IsAiWorking);
    }

    [Fact]
    public void ToggleSettings_ReturnsToTheTabThatWasShowing()
    {
        connection.Setup(x => x.ListFiles(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
        WorkspaceViewModel model = new(connection.Object, workspace);
        model.ShowFilesCommand.Execute(null);

        model.ToggleSettingsCommand.Execute(null);
        Assert.True(model.ShowSettings);
        model.ToggleSettingsCommand.Execute(null);

        Assert.True(model.ShowFiles);
        Assert.False(model.ShowSettings);
    }

    [Fact]
    public void Tabs_SwitchBetweenChatFilesAndSettings()
    {
        WorkspaceViewModel model = new(connection.Object, workspace);

        Assert.True(model.ShowChat);
        model.ToggleSettingsCommand.Execute(null);
        Assert.True(model.ShowSettings);
        Assert.False(model.ShowChat);
        Assert.False(model.ShowFiles);
        model.ShowChatCommand.Execute(null);
        Assert.True(model.ShowChat);
    }

    [Fact]
    public void Settings_DefaultToSonnetOnHigh()
    {
        WorkspaceViewModel model = new(connection.Object, workspace);

        Assert.Equal(AiModel.Sonnet, model.Settings.Model);
        Assert.Equal(AiEffort.High, model.Settings.Effort);
    }

    [Fact]
    public async Task Settings_ChangingModelAndEffortSavesToServer()
    {
        WorkspaceInfo updated = workspace with { Model = AiModel.Opus, Effort = AiEffort.Max };
        connection.Setup(x => x.ConfigureWorkspace(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.High, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Model = AiModel.Opus });
        connection.Setup(x => x.ConfigureWorkspace(workspace.Id, AiService.ClaudeCode, AiModel.Opus, AiEffort.Max, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        WorkspaceViewModel model = new(connection.Object, workspace);
        List<WorkspaceInfo> changes = [];
        model.Settings.Changed += changes.Add;

        model.Settings.Model = AiModel.Opus;
        await Task.Delay(50);
        model.Settings.Effort = AiEffort.Max;
        await Task.Delay(50);

        Assert.Equal(updated, changes[^1]);
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public async Task Settings_ChangingTheServiceOffersItsModelsAndSavesItsDefaults()
    {
        WorkspaceInfo codex = workspace with { Service = AiService.Codex, Model = AiModel.Terra, Effort = AiEffort.Medium };
        connection.Setup(x => x.ConfigureWorkspace(workspace.Id, AiService.Codex, AiModel.Terra, AiEffort.Medium, It.IsAny<CancellationToken>())).ReturnsAsync(codex);
        WorkspaceViewModel model = new(connection.Object, workspace);
        List<string?> raised = [];
        model.Settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        List<WorkspaceInfo> changes = [];
        model.Settings.Changed += changes.Add;

        Assert.Equal([AiModel.Sonnet, AiModel.Opus, AiModel.Haiku], model.Settings.Models);

        model.Settings.SelectedService = model.Settings.Services.First(choice => choice.Service == AiService.Codex);
        await Task.Delay(50);

        Assert.Equal([AiModel.Terra, AiModel.Sol, AiModel.Luna], model.Settings.Models);
        Assert.Equal((AiModel.Terra, AiEffort.Medium), (model.Settings.Model, model.Settings.Effort));
        Assert.Contains(nameof(SettingsViewModel.Models), raised);
        Assert.Equal(codex, Assert.Single(changes));
    }

    [Fact]
    public async Task Settings_FailedChangeIsRevertedWithError()
    {
        connection.Setup(x => x.ConfigureWorkspace(It.IsAny<Guid>(), It.IsAny<AiService>(), It.IsAny<AiModel>(), It.IsAny<AiEffort>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("busy"));
        WorkspaceViewModel model = new(connection.Object, workspace);

        model.Settings.Model = AiModel.Haiku;
        await Task.Delay(100);

        Assert.Equal(AiModel.Sonnet, model.Settings.Model);
        Assert.Equal("busy", model.Settings.Error);
    }

    [Fact]
    public void SendCommand_NeedsText()
    {
        WorkspaceViewModel model = new(connection.Object, workspace);

        Assert.False(model.Chat.SendCommand.CanExecute(null));
        model.Chat.Input = "x";
        Assert.True(model.Chat.SendCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("a.txt", false)]
    public async Task Files_PreviewMarkdownRenderedAndOtherFilesAsText(string path, bool markdown)
    {
        WorkspaceFiles root = new() { Path = string.Empty, Entries = [new FileEntry { Name = path, Path = path, IsFolder = false, Size = 3 }] };
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        connection.Setup(x => x.ReadFile(workspace.Id, path, It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = path, Text = "# Hi" });
        WorkspaceViewModel model = new(connection.Object, workspace);

        model.ShowFilesCommand.Execute(null);
        await Task.Delay(100);
        model.Files.Selected = model.Files.Nodes[0];
        await Task.Delay(100);

        Assert.Equal(markdown, model.Files.IsMarkdownPreview);
        Assert.Equal(!markdown, model.Files.IsTextPreview);
        model.Files.ClosePreviewCommand.Execute(null);
        Assert.False(model.Files.IsMarkdownPreview);
        Assert.False(model.Files.IsTextPreview);
    }

    [Fact]
    public async Task Files_ShowFoldersOpenedInPlaceAndWatchEveryOpenFolder()
    {
        WorkspaceFiles root = new() { Path = string.Empty, Entries = [new FileEntry { Name = "src", Path = "src", IsFolder = true }, new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false, Size = 3 }] };
        WorkspaceFiles src = new() { Path = "src", Parent = string.Empty, Entries = [new FileEntry { Name = "b.cs", Path = "src/b.cs", IsFolder = false }] };
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        connection.Setup(x => x.ListFiles(workspace.Id, "src", It.IsAny<CancellationToken>())).ReturnsAsync(src);
        connection.Setup(x => x.ReadFile(workspace.Id, "a.txt", It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = "a.txt", Text = "abc" });
        WorkspaceViewModel model = new(connection.Object, workspace);

        Assert.True(model.ShowChat);
        model.ShowFilesCommand.Execute(null);
        await Task.Delay(100);
        Assert.True(model.ShowFiles);
        Assert.Equal(["src", "a.txt"], model.Files.Nodes.Select(node => node.Name));
        Assert.True(model.Files.Nodes[0].Children[0].IsPlaceholder);
        connection.Verify(x => x.WatchFiles(workspace.Id, It.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { string.Empty })), It.IsAny<CancellationToken>()), Times.Once);

        model.Files.Selected = model.Files.Nodes[1];
        await Task.Delay(100);
        Assert.Equal("abc", model.Files.Preview?.Text);
        model.Files.ClosePreviewCommand.Execute(null);
        Assert.False(model.Files.IsPreviewing);

        model.Files.Selected = model.Files.Nodes[0];
        await Task.Delay(100);
        Assert.True(model.Files.Nodes[0].IsExpanded);
        Assert.Equal(["src/b.cs"], model.Files.Nodes[0].Children.Select(node => node.Path));
        Assert.Equal("/", model.Files.CurrentPath);
        connection.Verify(x => x.WatchFiles(workspace.Id, It.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { string.Empty, "src" })), It.IsAny<CancellationToken>()), Times.Once);

        model.Files.CollapseAllCommand.Execute(null);
        await Task.Delay(100);
        Assert.False(model.Files.Nodes[0].IsExpanded);
        connection.Verify(x => x.WatchFiles(workspace.Id, It.Is<IReadOnlyList<string>>(paths => paths.SequenceEqual(new[] { string.Empty })), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Files_EnterMakesAFolderTheRootAndUpGoesBack()
    {
        WorkspaceFiles root = new() { Path = string.Empty, Entries = [new FileEntry { Name = "src", Path = "src", IsFolder = true }] };
        WorkspaceFiles src = new() { Path = "src", Parent = string.Empty, Entries = [new FileEntry { Name = "b.cs", Path = "src/b.cs", IsFolder = false }] };
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        connection.Setup(x => x.ListFiles(workspace.Id, "src", It.IsAny<CancellationToken>())).ReturnsAsync(src);
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Files.Start();
        Assert.False(model.Files.UpCommand.CanExecute(null));

        await model.Files.Enter(model.Files.Nodes[0]);

        Assert.Equal("/src", model.Files.CurrentPath);
        Assert.Equal(["src/b.cs"], model.Files.Nodes.Select(node => node.Path));
        Assert.True(model.Files.UpCommand.CanExecute(null));
        model.Files.UpCommand.Execute(null);
        await Task.Delay(100);
        Assert.Equal(["src"], model.Files.Nodes.Select(node => node.Path));
    }

    [Fact]
    public async Task Files_RefreshAnOpenFolderWhenItChangesOnTheServer()
    {
        WorkspaceFiles root = new() { Path = string.Empty, Entries = [new FileEntry { Name = "src", Path = "src", IsFolder = true }] };
        WorkspaceFiles before = new() { Path = "src", Parent = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "src/a.txt", IsFolder = false }] };
        WorkspaceFiles after = new() { Path = "src", Parent = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "src/a.txt", IsFolder = false }, new FileEntry { Name = "b.txt", Path = "src/b.txt", IsFolder = false }] };
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        connection.SetupSequence(x => x.ListFiles(workspace.Id, "src", It.IsAny<CancellationToken>())).ReturnsAsync(before).ReturnsAsync(after);
        connection.SetupSequence(x => x.ReadFile(workspace.Id, "src/a.txt", It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = "src/a.txt", Text = "one" }).ReturnsAsync(new FileContent { Path = "src/a.txt", Text = "two" });
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Files.Start();
        model.Files.Selected = model.Files.Nodes[0];
        await Task.Delay(100);
        model.Files.Selected = model.Files.Nodes[0].Children[0];
        await Task.Delay(100);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = workspace.Id, Path = "other" });
        await Task.Delay(50);
        Assert.Single(model.Files.Nodes[0].Children);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = workspace.Id, Path = "src" });
        await Task.Delay(100);

        Assert.Equal(["src/a.txt", "src/b.txt"], model.Files.Nodes[0].Children.Select(node => node.Path));
        Assert.Equal("two", model.Files.Preview?.Text);
    }

    [Fact]
    public async Task Files_ClosePreviewWhenTheFileIsGone()
    {
        WorkspaceFiles root = new() { Path = string.Empty, Entries = [new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false, Size = 3 }] };
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(root);
        connection.SetupSequence(x => x.ReadFile(workspace.Id, "a.txt", It.IsAny<CancellationToken>())).ReturnsAsync(new FileContent { Path = "a.txt", Text = "one" }).ThrowsAsync(new RemoteException("gone"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Files.Start();
        model.Files.Selected = model.Files.Nodes[0];
        await Task.Delay(100);

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = workspace.Id, Path = string.Empty });
        await Task.Delay(100);

        Assert.False(model.Files.IsPreviewing);
    }
}
