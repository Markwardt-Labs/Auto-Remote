namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceGitTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private GitStatus status = new() { IsRepo = true, Branch = "main" };

    public WorkspaceGitTests()
    {
        connection.Setup(x => x.GetHistory(workspace.Id, null, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ChatHistory { Messages = [], Start = 0 });
        connection.Setup(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => status);
        connection.Setup(x => x.GetGitChanges(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.GetGitHistory(workspace.Id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync(new GitHistory { Commits = [] });
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
    }

    [Fact]
    public async Task HistoryTab_OnlyExistsForARepositoryAndSwitchesAwayWhenItStopsBeingOne()
    {
        status = new GitStatus { IsRepo = false };
        WorkspaceViewModel model = new(connection.Object, workspace);
        await model.Start();
        await Task.Delay(50);
        Assert.False(model.ShowHistoryTab);

        status = new GitStatus { IsRepo = true, Branch = "main" };
        await model.Git.Refresh();
        Assert.True(model.ShowHistoryTab);

        model.ShowHistoryCommand.Execute(null);
        await Task.Delay(100);
        Assert.True(model.ShowHistory);
        Assert.True(model.ShowHistoryView);
        Assert.True(model.ShowChatPanel);
        Assert.False(model.ShowChatView);
        Assert.False(model.ShowFilesPanel);
        connection.Verify(x => x.GetGitHistory(workspace.Id, 0, It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Once);

        status = new GitStatus { IsRepo = false };
        await model.Git.Refresh();
        Assert.True(model.ShowChat);
        Assert.False(model.ShowHistoryView);
    }

    [Fact]
    public async Task WithTabs_AFileOrDiffOpenedFromTheHistoryShowsOverIt()
    {
        connection.Setup(x => x.GetGitDiff(workspace.Id, "a.txt", "abc1234", It.IsAny<CancellationToken>())).ReturnsAsync(new GitDiff { Path = "a.txt", Lines = [] });
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Tabs);
        await model.Start();
        await Task.Delay(50);
        model.ShowHistoryCommand.Execute(null);
        await Task.Delay(100);
        Assert.False(model.ShowContentPanel);
        Assert.False(model.ShowSideTabs);

        model.Git.RequestDiff("a.txt", "abc1234");
        await Task.Delay(100);

        Assert.True(model.ShowContentPanel);
        model.Files.ClosePreviewCommand.Execute(null);
        Assert.False(model.ShowContentPanel);
    }

    [Fact]
    public async Task WithPanels_TheChatPanelGetsTabsForChatAndHistoryAndTheContentPanelStaysPut()
    {
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await Task.Delay(50);

        Assert.True(model.ShowSideTabs);
        Assert.True(model.ShowChatView);
        Assert.True(model.ShowFilesPanel);
        Assert.True(model.ShowContentPanel);

        model.ShowHistoryCommand.Execute(null);
        await Task.Delay(100);
        Assert.True(model.ShowHistoryView);
        Assert.False(model.ShowChatView);
        Assert.True(model.ShowFilesPanel);
        Assert.True(model.ShowContentPanel);

        model.ShowChatCommand.Execute(null);
        Assert.True(model.ShowChatView);
        Assert.False(model.ShowHistoryView);
    }

    [Fact]
    public async Task WithPanels_AWorkspaceWithoutARepositoryHasNoSideTabs()
    {
        status = new GitStatus { IsRepo = false };
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await Task.Delay(50);

        Assert.False(model.ShowSideTabs);
        Assert.True(model.ShowChatView);
    }

    [Fact]
    public async Task Focus_ReadsTheGitStateAgain()
    {
        WorkspaceViewModel model = new(connection.Object, workspace, ClientLayout.Panels);
        await model.Start();
        await Task.Delay(50);
        connection.Invocations.Clear();

        await model.Focus();
        await Task.Delay(50);

        connection.Verify(x => x.GetGitStatus(workspace.Id, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
