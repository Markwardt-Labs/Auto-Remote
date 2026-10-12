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

        WorkspaceFiles root = await browser.List(Workspace(temp), string.Empty, false, CancellationToken.None);

        Assert.Equal(["zdir", "a.txt"], root.Entries.Select(entry => entry.Name));
        Assert.Equal(3, root.Entries[1].Size);
        Assert.Null(root.Parent);

        File.WriteAllText(temp.Combine("zdir/inner.txt"), "x");
        WorkspaceFiles inner = await browser.List(Workspace(temp), "zdir", false, CancellationToken.None);

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

        await Assert.ThrowsAsync<RequestException>(() => browser.List(Workspace(temp), path, false, CancellationToken.None));
    }

    [Fact]
    public async Task List_RejectsSiblingFolderWithSamePrefix()
    {
        using TempFolder temp = new();
        string sibling = temp.Path + "-other";
        Directory.CreateDirectory(sibling);

        try
        {
            await Assert.ThrowsAsync<RequestException>(() => browser.List(Workspace(temp), "../" + Path.GetFileName(sibling), false, CancellationToken.None));
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

    [Fact]
    public async Task List_HidesWhatAFileHideMatchesUnlessShowingHidden()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("bin"));
        Directory.CreateDirectory(temp.Combine("src/gen"));
        File.WriteAllText(temp.Combine(".filehide"), "# comment\nbin/\n*.log\n");
        File.WriteAllText(temp.Combine("src/.filehide"), "gen/\n!keep.log\n");
        File.WriteAllText(temp.Combine("a.log"), "x");
        File.WriteAllText(temp.Combine("b.txt"), "x");
        File.WriteAllText(temp.Combine("src/keep.log"), "x");
        File.WriteAllText(temp.Combine("src/c.cs"), "x");

        Assert.Equal(["src", ".filehide", "b.txt"], (await browser.List(Workspace(temp), string.Empty, false, CancellationToken.None)).Entries.Select(entry => entry.Name));
        Assert.Equal([".filehide", "c.cs", "keep.log"], (await browser.List(Workspace(temp), "src", false, CancellationToken.None)).Entries.Select(entry => entry.Name));
        Assert.Equal(["bin", "src", ".filehide", "a.log", "b.txt"], (await browser.List(Workspace(temp), string.Empty, true, CancellationToken.None)).Entries.Select(entry => entry.Name));
        Assert.Empty((await browser.List(Workspace(temp), "bin", false, CancellationToken.None)).Entries);
    }

    [Fact]
    public async Task Search_FindsFilesByFuzzyNameBestFirstAndSkipsGitAndHiddenFiles()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine(".git"));
        Directory.CreateDirectory(temp.Combine("src"));
        Directory.CreateDirectory(temp.Combine("gen"));
        File.WriteAllText(temp.Combine(".git/config"), "x");
        File.WriteAllText(temp.Combine(".filehide"), "gen/\n");
        File.WriteAllText(temp.Combine("gen/config.cs"), "x");
        File.WriteAllText(temp.Combine("src/config.cs"), "x");
        File.WriteAllText(temp.Combine("src/readme.md"), "x");
        File.WriteAllText(temp.Combine("cfg.txt"), "x");

        IReadOnlyList<FileSearchResult> found = await browser.Search(Workspace(temp), "cfg", false, false, CancellationToken.None);
        Assert.Equal(["cfg.txt", "src/config.cs"], found.Select(result => result.Path));
        Assert.All(found, result => Assert.Equal(0, result.Line));

        IReadOnlyList<FileSearchResult> all = await browser.Search(Workspace(temp), "config", false, true, CancellationToken.None);
        Assert.Equal(["gen/config.cs", "src/config.cs"], all.Select(result => result.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Search_ContentFindsMatchingLinesIgnoringCaseAndBinaryFiles()
    {
        using TempFolder temp = new();
        File.WriteAllText(temp.Combine("a.txt"), "one\n  Needle here  \nthree");
        File.WriteAllBytes(temp.Combine("b.bin"), [110, 101, 101, 100, 108, 101, 0, 1]);
        File.WriteAllText(temp.Combine("c.txt"), "nothing");

        IReadOnlyList<FileSearchResult> found = await browser.Search(Workspace(temp), "needle", true, false, CancellationToken.None);

        FileSearchResult hit = Assert.Single(found);
        Assert.Equal("a.txt", hit.Path);
        Assert.Equal(2, hit.Line);
        Assert.Equal("Needle here", hit.Text);
    }
}
