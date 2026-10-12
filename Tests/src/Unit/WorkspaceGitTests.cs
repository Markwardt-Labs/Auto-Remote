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
        connection.Setup(x => x.ListFiles(workspace.Id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [] });
    }

}
