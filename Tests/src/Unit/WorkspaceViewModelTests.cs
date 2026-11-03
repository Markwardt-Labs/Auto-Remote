namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private ChatHistory History(string? older, bool working, params string[] texts) => new() { Older = older, Working = working, Messages = [.. texts.Select(text => new ChatMessage { Role = MessageRole.Assistant, Text = text })] };

    private RemoteEvent Added(string text, MessageRole role = MessageRole.Assistant) => new() { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Message = new ChatMessage { Role = role, Text = text } };

    private RemoteEvent Working(bool working) => new() { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = workspace.Id, Working = working };

    [Fact]
    public async Task Chat_KeepsEventsThatArriveWhileTheHistoryLoads()
    {
        TaskCompletionSource<ChatHistory> loading = new();
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(loading.Task);
        WorkspaceViewModel model = new(connection.Object, workspace);

        Task start = model.Start();
        connection.Raise(x => x.EventReceived += null, Added("old"));
        connection.Raise(x => x.EventReceived += null, Added("new"));
        loading.SetResult(History(null, false, "older", "old"));
        await start;

        Assert.Equal(["older", "old", "new"], model.Chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Chat_KeepsARepeatedMessageThatTheHistoryDoesNotAlreadyHold()
    {
        TaskCompletionSource<ChatHistory> loading = new();
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).Returns(loading.Task);
        WorkspaceViewModel model = new(connection.Object, workspace);

        Task start = model.Start();
        connection.Raise(x => x.EventReceived += null, Added("ok"));
        connection.Raise(x => x.EventReceived += null, Added("ok"));
        loading.SetResult(History(null, false, "ok"));
        await start;

        Assert.Equal(["ok", "ok"], model.Chat.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Chat_ReadsTheNewestMessagesAgainAfterTheConnectionCameBackWithoutDuplicatingAnything()
    {
        connection.SetupSequence(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(History(null, false, "one"))
            .ReturnsAsync(History(null, true, "one", "two"));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        await model.Chat.Resync();
        connection.Raise(x => x.EventReceived += null, Added("three"));

        Assert.Equal(["one", "two", "three"], model.Chat.Messages.Select(message => message.Text));
        Assert.True(model.Chat.IsAiWorking);
    }

    [Fact]
    public async Task Chat_LoadsOlderChunksOnDemandUntilTheBeginning()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History("before-e", false, "e", "f"));
        connection.Setup(x => x.GetHistory(workspace.Id, "before-e", It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History("before-b", false, "b", "c", "d"));
        connection.Setup(x => x.GetHistory(workspace.Id, "before-b", It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(null, false, "a"));
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

        connection.Raise(x => x.EventReceived += null, Added("g"));
        Assert.Equal("g", model.Chat.Messages[^1].Text);
    }

    [Fact]
    public async Task Chat_ShowsFailuresTheAiReports()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(null, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspace.Id, Error = "out of tokens" });

        Assert.Equal("out of tokens", model.Chat.Error);
    }

    [Fact]
    public async Task IsAiWorking_FollowsTheServersWorkingEvents()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(null, false));
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
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(null, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        model.Dispose();
        connection.Raise(x => x.EventReceived += null, Working(true));

        Assert.False(model.IsAiWorking);
    }

    [Fact]
    public void ToggleSettings_ReturnsToTheTabThatWasShowing()
    {
        connection.Setup(x => x.ListFiles(workspace.Id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
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
    public async Task Settings_ShowTheModelAndSettingsOfTheAiSession()
    {
        connection.Setup(x => x.ListModels(AiService.ClaudeCode, It.IsAny<CancellationToken>())).ReturnsAsync([new ModelInfo { Id = "sonnet", Name = "Sonnet 5.5", Settings = [new ModelSettingInfo { Id = "effort", Name = "Effort", Kind = ModelSettingKind.Choice, Choices = ["low", "medium", "high"], Default = "medium" }] }]);

        connection.Setup(x => x.GetWorkspaceAi(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceAi { Model = "sonnet", ModelSettings = new Dictionary<string, string> { ["effort"] = "high" } });

        WorkspaceViewModel model = new(connection.Object, workspace);
        for (int attempt = 0; attempt < 100 && model.Settings.Settings.Count == 0; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal("sonnet", model.Settings.SelectedModel?.Id);
        Assert.Equal("high", model.Settings.Settings.Single().ChoiceValue);
    }

    [Fact]
    public void SendCommand_NeedsText()
    {
        WorkspaceViewModel model = new(connection.Object, workspace);

        Assert.False(model.Chat.SendCommand.CanExecute(null));
        model.Chat.Input = "x";
        Assert.True(model.Chat.SendCommand.CanExecute(null));
    }

}
