namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class WorkspaceWatcherTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly List<RemoteEvent> published = [];

    private readonly Mock<IEventHub> hub = new();

    private readonly WorkspaceInfo workspace;

    private readonly WorkspaceWatcher watcher;

    public WorkspaceWatcherTests()
    {
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = temp.Path };
        hub.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            lock (published)
            {
                published.Add(remoteEvent);
            }
        });
        watcher = new WorkspaceWatcher(hub.Object, Mock.Of<ILog>(), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(400));
    }

    public void Dispose() => temp.Dispose();

    private List<RemoteEvent> Events()
    {
        lock (published)
        {
            return [.. published];
        }
    }

    private List<string> Folders() => [.. Events().Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged).Select(remoteEvent => remoteEvent.Path!)];

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private void Clear()
    {
        lock (published)
        {
            published.Clear();
        }
    }

    [Fact]
    public async Task Watch_ReportsEachFolderWhereEntriesChangedAnywhereBelowTheWorkspaceFolderOnce()
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "src", "deep"));
        using IDisposable lease = watcher.Watch(workspace);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "deep", "a.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "src", "deep", "b.txt"), "2");
        await File.WriteAllTextAsync(temp.Combine("top.txt"), "3");

        await Until(() => Folders().Contains("src/deep") && Folders().Contains(string.Empty));
        await Task.Delay(250);
        Assert.Equal(1, Folders().Count(folder => folder == "src/deep"));
        Assert.All(Events(), remoteEvent => Assert.Equal(workspace.Id, remoteEvent.WorkspaceId));
    }

    [Fact]
    public async Task Watch_SeesFolderCreationDeletionAndContentChangesOfFilesThatAlreadyExisted()
    {
        await File.WriteAllTextAsync(temp.Combine("existing.txt"), "old");
        using IDisposable lease = watcher.Watch(workspace);

        await File.WriteAllTextAsync(temp.Combine("existing.txt"), "new content");
        await Until(() => Folders().Contains(string.Empty));
        Clear();
        Directory.CreateDirectory(temp.Combine("made"));
        await Until(() => Folders().Contains(string.Empty));
        Clear();
        Directory.Delete(temp.Combine("made"));

        await Until(() => Folders().Contains(string.Empty));
    }

    [Fact]
    public async Task Watch_NeverWaitsLongerThanTheLongestWaitWhileFilesKeepChanging()
    {
        using IDisposable lease = watcher.Watch(workspace);
        DateTime begin = DateTime.UtcNow;
        using CancellationTokenSource stop = new();
        Task writing = Task.Run(async () =>
        {
            for (int count = 0; !stop.IsCancellationRequested; count++)
            {
                await File.WriteAllTextAsync(temp.Combine("busy.txt"), count.ToString(CultureInfo.InvariantCulture));
                await Task.Delay(20);
            }
        });

        await Until(() => Events().Count > 0);
        TimeSpan first = DateTime.UtcNow - begin;
        await stop.CancelAsync();
        await writing;

        Assert.True(first < TimeSpan.FromMilliseconds(1500), first.ToString());
    }

    [Fact]
    public async Task Watch_IgnoresWhatHappensInsideTheGitFolderExceptItsHeadIndexAndRefsWhichSayGitChanged()
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git", "objects", "ab"));
        Directory.CreateDirectory(Path.Combine(temp.Path, ".git", "refs", "heads"));
        using IDisposable lease = watcher.Watch(workspace);

        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".git", "objects", "ab", "cdef"), "blob");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".git", "refs", "heads", "main.lock"), "x");
        await Task.Delay(300);
        Assert.Empty(Events());

        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".git", "refs", "heads", "main"), "abc");
        await Until(() => Events().Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.GitChanged));
        Clear();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, ".git", "HEAD"), "ref: refs/heads/main");
        await Until(() => Events().Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.GitChanged));

        Assert.DoesNotContain(Events(), remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged);
    }

    [Fact]
    public async Task Watch_SaysEverythingMayHaveChangedWhenTooManyFoldersChangedToListThem()
    {
        for (int index = 0; index < 250; index++)
        {
            Directory.CreateDirectory(temp.Combine($"d{index}"));
        }

        using IDisposable lease = watcher.Watch(workspace);
        await Task.Delay(100);
        Clear();

        for (int index = 0; index < 250; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(temp.Path, $"d{index}", "f.txt"), "x");
        }

        await Until(() => Events().Any(remoteEvent => remoteEvent.Text == "all"));
        await Task.Delay(300);

        Assert.Equal(string.Empty, Events().First(remoteEvent => remoteEvent.Text == "all").Path);
        Assert.True(Folders().Count < 250);
    }

    [Fact]
    public async Task Watch_KeepsWatchingForAsLongAsAnyoneHasNotLeftAndStopsAfterTheLast()
    {
        IDisposable first = watcher.Watch(workspace);
        IDisposable second = watcher.Watch(workspace);

        first.Dispose();
        first.Dispose();
        await File.WriteAllTextAsync(temp.Combine("a.txt"), "1");
        await Until(() => Folders().Contains(string.Empty));
        second.Dispose();
        await Task.Delay(100);
        Clear();
        await File.WriteAllTextAsync(temp.Combine("b.txt"), "2");
        await Task.Delay(300);

        Assert.Empty(Events());
    }

    [Fact]
    public async Task Forget_StopsTheWatchForEveryoneAndAFreshWatchStartsAnother()
    {
        using IDisposable lease = watcher.Watch(workspace);

        watcher.Forget(workspace.Id);
        await File.WriteAllTextAsync(temp.Combine("a.txt"), "1");
        await Task.Delay(300);
        Assert.Empty(Events());

        using IDisposable again = watcher.Watch(workspace);
        await File.WriteAllTextAsync(temp.Combine("b.txt"), "2");
        await Until(() => Folders().Contains(string.Empty));
    }

    [Fact]
    public void Watch_OfAFolderThatIsGoneDoesNotFailButSaysSo()
    {
        Mock<ILog> log = new();
        WorkspaceWatcher missing = new(hub.Object, log.Object);

        using IDisposable lease = missing.Watch(workspace with { Folder = temp.Combine("gone") });

        log.Verify(x => x.Write(It.Is<string>(message => message.Contains("Could not watch", StringComparison.Ordinal))), Times.Once);
    }
}
