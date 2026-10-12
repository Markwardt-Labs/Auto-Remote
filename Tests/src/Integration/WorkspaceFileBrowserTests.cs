namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class WorkspaceFileBrowserTests
{
    private readonly WorkspaceFileBrowser browser = new();

    private static WorkspaceInfo Workspace(TempFolder temp) => new() { Id = Guid.NewGuid(), Folder = temp.Path };

    [Fact]
    public async Task List_ShowsFoldersBeforeFilesWithRelativePaths()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("zdir"));
        File.WriteAllText(temp.Combine("a.txt"), "abc");

        WorkspaceFiles root = await browser.List(Workspace(temp), string.Empty, CancellationToken.None);

        Assert.Equal(["zdir", "a.txt"], root.Entries.Select(entry => entry.Name));
        Assert.Equal(3, root.Entries[1].Size);
        Assert.Null(root.Parent);

        File.WriteAllText(temp.Combine("zdir/inner.txt"), "x");
        WorkspaceFiles inner = await browser.List(Workspace(temp), "zdir", CancellationToken.None);

        Assert.Equal("zdir", inner.Path);
        Assert.Equal(string.Empty, inner.Parent);
        Assert.Equal("zdir/inner.txt", inner.Entries[0].Path);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("sub/../..")]
    public async Task List_RejectsEscapingTheWorkspaceFolder(string path)
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("sub"));

        await Assert.ThrowsAsync<RequestException>(() => browser.List(Workspace(temp), path, CancellationToken.None));
    }

    [Fact]
    public async Task List_RejectsSiblingFolderWithSamePrefix()
    {
        using TempFolder temp = new();
        string sibling = temp.Path + "-other";
        Directory.CreateDirectory(sibling);

        try
        {
            await Assert.ThrowsAsync<RequestException>(() => browser.List(Workspace(temp), "../" + Path.GetFileName(sibling), CancellationToken.None));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    public async Task Read_ReturnsText()
    {
        using TempFolder temp = new();
        File.WriteAllText(temp.Combine("a.txt"), "héllo");

        FileContent content = await browser.Read(Workspace(temp), "a.txt", CancellationToken.None);

        Assert.Equal("héllo", content.Text);
        Assert.False(content.Truncated);
    }

    [Fact]
    public async Task Read_TruncatesLargeFiles()
    {
        using TempFolder temp = new();
        File.WriteAllText(temp.Combine("big.txt"), new string('a', 300 * 1024));

        FileContent content = await browser.Read(Workspace(temp), "big.txt", CancellationToken.None);

        Assert.True(content.Truncated);
        Assert.Equal(256 * 1024, content.Text.Length);
    }

    [Fact]
    public async Task Read_RejectsBinaryAndEscapingAndMissing()
    {
        using TempFolder temp = new();
        File.WriteAllBytes(temp.Combine("b.bin"), [1, 0, 2]);

        await Assert.ThrowsAsync<RequestException>(() => browser.Read(Workspace(temp), "b.bin", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => browser.Read(Workspace(temp), "../etc/passwd", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => browser.Read(Workspace(temp), "missing.txt", CancellationToken.None));
    }

    [Fact]
    public async Task Watch_ReportsChangesToTheFolderOnceAfterTheyQuietDownAndStopsWhenDisposed()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("src"));
        List<string> changes = [];
        IDisposable watch = browser.Watch(Workspace(temp), "src", path =>
        {
            lock (changes)
            {
                changes.Add(path);
            }
        });

        for (int index = 0; index < 5; index++)
        {
            await File.WriteAllTextAsync(temp.Combine($"src/f{index}.txt"), "x");
        }

        await Until(() => changes.Count > 0, changes);
        await Task.Delay(600);
        lock (changes)
        {
            Assert.Equal(["src"], changes);
        }

        watch.Dispose();
        await File.WriteAllTextAsync(temp.Combine("src/after.txt"), "x");
        await Task.Delay(600);
        lock (changes)
        {
            Assert.Single(changes);
        }
    }

    [Fact]
    public void Watch_RefusesPathsOutsideOrMissing()
    {
        using TempFolder temp = new();

        Assert.Throws<RequestException>(() => browser.Watch(Workspace(temp), "..", _ => { }));
        Assert.Throws<RequestException>(() => browser.Watch(Workspace(temp), "missing", _ => { }));
    }

    private static async Task Until(Func<bool> condition, object gate)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            lock (gate)
            {
                if (condition())
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out.");
    }
}
