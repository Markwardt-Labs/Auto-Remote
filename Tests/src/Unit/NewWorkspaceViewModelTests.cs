namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class NewWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing home = new()
    {
        Path = "/home/me",
        Parent = "/home",
        Folders = [new FolderEntry { Name = "code", Path = "/home/me/code" }],
    };

    public NewWorkspaceViewModelTests() => connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(home);

    [Fact]
    public async Task Open_ShowsHomeFolder()
    {
        NewWorkspaceViewModel model = new(connection.Object);

        await model.Open();

        Assert.Equal("/home/me", model.Browser.CurrentPath);
        Assert.Equal(home.Folders, model.Browser.Folders);
        Assert.True(model.Browser.CanGoUp);
        Assert.True(model.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Cancel_RaisesCancelled()
    {
        NewWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
