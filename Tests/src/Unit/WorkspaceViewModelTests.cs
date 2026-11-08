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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(History(null, false));
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();

        connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = workspace.Id, Error = "out of tokens" });

        Assert.Equal("out of tokens", model.Chat.Error);
    }

    [Fact]
    public async Task IsAiWorking_FollowsTheServersWorkingEvents()
    {
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
        connection.Setup(x => x.GetSavedInputs(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
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
    public async Task Settings_ShowHowTheAiSessionRuns()
    {
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceSettings { Capability = AiCapability.Max, Effort = AiEffort.Low });

        WorkspaceViewModel model = new(connection.Object, workspace);
        for (int attempt = 0; attempt < 100 && model.Settings.SelectedCapability.Value != AiCapability.Max; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Equal(AiCapability.Max, model.Settings.SelectedCapability.Value);
        Assert.Equal(AiEffort.Low, model.Settings.SelectedEffort.Value);
    }

    private async Task<WorkspaceViewModel> OpenSettings(Func<WorkspaceSettings, Task>? onSet = null)
    {
        WorkspaceSettings stored = new() { Capability = AiCapability.Balanced, Effort = AiEffort.High };
        connection.Setup(x => x.GetWorkspaceSettings(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(stored);
        connection.Setup(x => x.SetWorkspaceSettings(workspace.Id, It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>())).Returns(async (Guid _, WorkspaceSettings given, CancellationToken _) =>
        {
            if (onSet is not null)
            {
                await onSet(given);
            }

            return given;
        });
        WorkspaceViewModel model = new(connection.Object, workspace);
        for (int attempt = 0; attempt < 100 && model.Settings.SelectedEffort.Value != AiEffort.High; attempt++)
        {
            await Task.Delay(20);
        }

        model.ToggleSettingsCommand.Execute(null);
        return model;
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task ClosingTheSettings_SendsWhatWasEditedOnceAndGoesBackToTheTabItCameFrom()
    {
        WorkspaceViewModel model = await OpenSettings();
        model.Settings.SelectedEffort = model.Settings.Efforts.Single(choice => choice.Value == AiEffort.Low);
        model.Settings.IsShellAllowed = true;
        connection.Invocations.Clear();

        model.ToggleSettingsCommand.Execute(null);
        await Until(() => !model.ShowSettings);

        connection.Verify(x => x.SetWorkspaceSettings(workspace.Id, It.Is<WorkspaceSettings>(sent => sent.Effort == AiEffort.Low && sent.IsShellAllowed), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.Settings.HasPending);
    }

    [Fact]
    public async Task ClosingTheSettings_WithoutEditsSendsNothing()
    {
        WorkspaceViewModel model = await OpenSettings();

        model.ToggleSettingsCommand.Execute(null);
        await Until(() => !model.ShowSettings);

        connection.Verify(x => x.SetWorkspaceSettings(It.IsAny<Guid>(), It.IsAny<WorkspaceSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ClosingTheSettings_StaysOnThemShowingWhyWhenTheServerRefusesTheEdits()
    {
        WorkspaceViewModel model = await OpenSettings(_ => throw new RemoteException("System access cannot be turned off with this provider."));
        model.Settings.IsSystemAccessAllowed = true;

        model.ToggleSettingsCommand.Execute(null);
        await Until(() => model.Settings.HasError);

        Assert.True(model.ShowSettings);
        Assert.Equal("System access cannot be turned off with this provider.", model.Settings.Error);
        Assert.True(model.Settings.HasPending);
    }

    [Fact]
    public async Task LeavingTheSettingsByAnotherTab_AppliesTheEditsToo()
    {
        WorkspaceViewModel model = await OpenSettings();
        model.Settings.IsWebAllowed = true;

        model.ShowChatCommand.Execute(null);
        await Until(() => !model.Settings.HasPending);

        connection.Verify(x => x.SetWorkspaceSettings(workspace.Id, It.Is<WorkspaceSettings>(sent => sent.IsWebAllowed), It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(model.ShowSettings);
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
