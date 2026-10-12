namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class CloneWorkspaceViewModelTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly FolderListing work = new() { Path = "/work", Parent = "/", Folders = [] };

    public CloneWorkspaceViewModelTests() => connection.Setup(x => x.ListFolders(null, It.IsAny<CancellationToken>())).ReturnsAsync(work);

    [Fact]
    public async Task Cancel_RaisesCancelled()
    {
        CloneWorkspaceViewModel model = new(connection.Object);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;
        await model.Open();

        model.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }
}
