namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ErrorHistoryViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/first" };

    private readonly WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/second" };

    private static ErrorEntry Error(string message, int minute) => new() { Time = new DateTimeOffset(2026, 11, 20, 9, minute, 0, TimeSpan.Zero), Message = message };

    private static RemoteEvent Changed(Guid id, params ErrorEntry[] errors) => new() { Kind = RemoteEventKind.ErrorsChanged, WorkspaceId = id, Errors = errors };

    private static RemoteEvent Failed(Guid id, string message) => new() { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = message };

    private async Task<WorkspaceViewModel> Open(WorkspaceInfo workspace, params ErrorEntry[] errors)
    {
        connection.Setup(x => x.GetErrors(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(errors);
        connection.Setup(x => x.GetSavedInputs(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [] });
        connection.Setup(x => x.GetScripts(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new ScriptRuns { Runs = [] });
        connection.Setup(x => x.GetWorkspaceState(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(WorkspaceState.Ready);
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await Until(() => model.Errors.Entries.Count == errors.Length);
        return model;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Load_ShowsTheErrorsOfTheServerNewestFirstAndSaysWhenThereAreNone()
    {
        connection.Setup(x => x.GetErrors(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync([Error("newer", 30), Error("older", 5)]);
        ErrorHistoryViewModel model = new(connection.Object, first.Id);
        Assert.True(model.HasNoErrors);

        await model.Load();

        Assert.Equal(["newer", "older"], model.Entries.Select(entry => entry.Message));
        Assert.False(model.HasNoErrors);

        connection.Setup(x => x.GetErrors(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        await model.Load();

        Assert.Empty(model.Entries);
        Assert.True(model.HasNoErrors);
    }

    [Fact]
    public async Task Load_ShowsWhyTheErrorsCouldNotBeReadAndKeepsWhatItShowed()
    {
        connection.SetupSequence(x => x.GetErrors(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync([Error("kept", 1)]).ThrowsAsync(new RemoteException("The server is busy."));
        ErrorHistoryViewModel model = new(connection.Object, first.Id);
        await model.Load();

        await model.Load();

        Assert.Equal("The server is busy.", model.Error);
        Assert.Equal(["kept"], model.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public void Receive_ReplacesTheListWithTheHistoryOfTheErrorEventOfItsOwnWorkspace()
    {
        ErrorHistoryViewModel model = new(connection.Object, first.Id);

        model.Receive(Changed(first.Id, Error("second", 2), Error("first", 1)));

        Assert.Equal(["second", "first"], model.Entries.Select(entry => entry.Message));
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 2, 0, TimeSpan.Zero), model.Entries[0].Time);
    }

    [Fact]
    public void Receive_IgnoresTheErrorsOfOtherWorkspacesAndEventsThatAreNotErrorHistories()
    {
        ErrorHistoryViewModel model = new(connection.Object, first.Id);

        model.Receive(Changed(second.Id, Error("of the other workspace", 1)));
        model.Receive(Failed(first.Id, "a failure is not the history"));
        model.Receive(new RemoteEvent { Kind = RemoteEventKind.ErrorsChanged, WorkspaceId = first.Id });

        Assert.Empty(model.Entries);
    }

    [Fact]
    public async Task AnErrorOfOneOpenWorkspaceShowsOnlyThere()
    {
        WorkspaceViewModel one = await Open(first);
        WorkspaceViewModel other = await Open(second);

        connection.Raise(x => x.EventReceived += null, Failed(first.Id, "the AI could not be started"));
        connection.Raise(x => x.EventReceived += null, Changed(first.Id, Error("the AI could not be started", 3)));

        Assert.Equal("the AI could not be started", one.Chat.Error);
        Assert.Equal(["the AI could not be started"], one.Errors.Entries.Select(entry => entry.Message));
        Assert.Null(other.Chat.Error);
        Assert.Empty(other.Errors.Entries);
        Assert.True(other.Errors.HasNoErrors);

        connection.Raise(x => x.EventReceived += null, Changed(second.Id, Error("only in the second", 4)));

        Assert.Equal(["only in the second"], other.Errors.Entries.Select(entry => entry.Message));
        Assert.Equal(["the AI could not be started"], one.Errors.Entries.Select(entry => entry.Message));
    }

    [Fact]
    public async Task OpeningTheSettingsReadsTheErrorsAgainAndAReconnectDoes()
    {
        WorkspaceViewModel model = await Open(first, Error("before", 1));
        connection.Setup(x => x.GetErrors(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync([Error("purged and replaced", 2)]);

        model.ToggleSettingsCommand.Execute(null);
        await Until(() => model.Errors.Entries.Any(entry => entry.Message == "purged and replaced"));

        Assert.Equal(["purged and replaced"], model.Errors.Entries.Select(entry => entry.Message));

        connection.Setup(x => x.GetErrors(first.Id, It.IsAny<CancellationToken>())).ReturnsAsync([Error("after the reconnect", 3)]);
        connection.Raise(x => x.Reconnected += null);
        await Until(() => model.Errors.Entries.Any(entry => entry.Message == "after the reconnect"));

        Assert.Equal(["after the reconnect"], model.Errors.Entries.Select(entry => entry.Message));
    }
}
