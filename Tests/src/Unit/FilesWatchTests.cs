namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FilesWatchTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private string text = "one";

    private int reads;

    private FileEntry Entry(string path, bool folder) => new() { Name = path.Split('/')[^1], Path = path, IsFolder = folder };

    private FilesViewModel Create(TimeSpan interval)
    {
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [Entry("src", true), Entry("top.txt", false)] });
        connection.Setup(x => x.ListFiles(id, "src", It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = "src", Parent = string.Empty, Entries = [Entry("src/a.txt", false)] });
        connection.Setup(x => x.ReadFile(id, "src/a.txt", It.IsAny<CancellationToken>())).ReturnsAsync(() =>
        {
            Interlocked.Increment(ref reads);
            return new FileContent { Path = "src/a.txt", Text = text };
        });
        return new FilesViewModel(connection.Object, id, new GitViewModel(connection.Object, id), true, interval);
    }

    private void Changed(string path, string? note = null) => connection.Raise(x => x.EventReceived += null, new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = id, Path = path, Text = note });

    private async Task<FilesViewModel> Previewing(TimeSpan interval)
    {
        FilesViewModel files = Create(interval);
        await files.Start();
        await files.Choose(files.Nodes.Single(node => node.Path == "src"));
        await Task.Delay(100);
        await files.Choose(files.Nodes.Single(node => node.Path == "src").Children.Single());
        return files;
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Start_TellsTheServerWhichWorkspaceIsOpenOnce()
    {
        FilesViewModel files = Create(TimeSpan.FromSeconds(3));

        await files.Start();
        Changed("src");
        await Task.Delay(100);

        connection.Verify(x => x.WatchWorkspace(id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FilesChanged_ReloadsTheOpenFileWhenItsContentChangedOutsideTheClient()
    {
        FilesViewModel files = await Previewing(TimeSpan.Zero);
        text = "two";

        Changed("src");
        await Until(() => files.Preview?.Text == "two");

        Assert.Equal("two", files.Preview!.Text);
    }

    [Fact]
    public async Task FilesChanged_ReloadsTheOpenFileAtMostOncePerIntervalNoMatterHowOftenItChanges()
    {
        FilesViewModel files = await Previewing(TimeSpan.FromMilliseconds(600));
        int before = reads;
        text = "two";

        for (int count = 0; count < 20; count++)
        {
            Changed("src");
            await Task.Delay(10);
        }

        await Until(() => files.Preview?.Text == "two");
        await Task.Delay(200);

        Assert.Equal(1, reads - before);
    }

    [Fact]
    public async Task FilesChanged_ReadsTheFileAgainAfterTheIntervalWhenItChangesAgain()
    {
        FilesViewModel files = await Previewing(TimeSpan.FromMilliseconds(300));
        text = "two";
        Changed("src");
        await Until(() => files.Preview?.Text == "two");
        DateTimeOffset first = DateTimeOffset.UtcNow;
        text = "three";

        Changed("src");
        await Until(() => files.Preview?.Text == "three");

        Assert.True(DateTimeOffset.UtcNow - first >= TimeSpan.FromMilliseconds(250) || reads >= 3);
    }

    [Fact]
    public async Task FilesChanged_LeavesThePreviewAloneWhenTheFileIsTheSameOrAnotherFolderChanged()
    {
        FilesViewModel files = await Previewing(TimeSpan.Zero);
        FileContent? shown = files.Preview;
        int before = reads;

        Changed("src");
        await Task.Delay(200);
        Changed(string.Empty);
        await Task.Delay(200);

        Assert.Same(shown, files.Preview);
        Assert.Equal(1, reads - before);
    }

    [Fact]
    public async Task FilesChanged_WithAllReadsTheTreeAndTheOpenFileAgain()
    {
        FilesViewModel files = await Previewing(TimeSpan.Zero);
        text = "two";
        connection.Invocations.Clear();

        Changed(string.Empty, "all");
        await Until(() => files.Preview?.Text == "two");

        connection.Verify(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        connection.Verify(x => x.ListFiles(id, "src", It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
