namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FilesGitTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private List<GitChange> changes = [new() { Path = "a.txt", Status = GitChangeStatus.Modified }, new() { Path = "new", Status = GitChangeStatus.Added }, new() { Path = "out/", Status = GitChangeStatus.Ignored }];

    public FilesGitTests()
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = true, Branch = "main" });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => changes);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles
        {
            Path = string.Empty,
            Entries =
            [
                new FileEntry { Name = "new", Path = "new", IsFolder = true },
                new FileEntry { Name = "out", Path = "out", IsFolder = true },
                new FileEntry { Name = "a.txt", Path = "a.txt", IsFolder = false },
                new FileEntry { Name = "b.txt", Path = "b.txt", IsFolder = false },
            ],
        });
    }

    private GitDiff Diff(string path, params GitDiffLine[] lines) => new() { Path = path, Lines = lines };

    [Fact]
    public async Task Nodes_AreColoredByHowGitSeesThemAndRecoloredWhenTheChangesUpdate()
    {
        GitViewModel git = new(connection.Object, id);
        FilesViewModel files = new(connection.Object, id, git);
        await git.Start();
        await files.Start();

        Assert.Equal([GitChangeStatus.Added, GitChangeStatus.Ignored, GitChangeStatus.Modified, null], files.Nodes.Select(node => node.GitStatus));
        Assert.True(files.Nodes[0].IsAdded);
        Assert.True(files.Nodes[1].IsIgnored);
        Assert.True(files.Nodes[2].IsModified);

        changes = [new GitChange { Path = "b.txt", Status = GitChangeStatus.Modified }];
        await git.Refresh();

        Assert.Equal([null, null, null, GitChangeStatus.Modified], files.Nodes.Select(node => node.GitStatus));
    }

}
