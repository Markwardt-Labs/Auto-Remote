namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceSnapshotsFailureTests
{
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(RequestException))]
    public async Task Capture_NeverThrowsWhenGitOrTheDiskFailsAndKeepsNoSnapshot(Type failure)
    {
        Mock<IGitService> git = new();
        git.Setup(x => x.Run(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>(), It.IsAny<TimeSpan?>())).ThrowsAsync((Exception)Activator.CreateInstance(failure, "boom")!);
        WorkspaceSnapshots snapshots = new(git.Object);
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

        await snapshots.Capture(workspace, CancellationToken.None);

        Assert.False(snapshots.Has(workspace.Id));
    }
}
