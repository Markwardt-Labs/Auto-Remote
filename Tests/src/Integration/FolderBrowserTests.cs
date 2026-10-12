namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class FolderBrowserTests
{
    private readonly FolderBrowser browser = new();

    [Fact]
    public async Task List_ReturnsSortedVisibleSubFolders()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("beta"));
        Directory.CreateDirectory(temp.Combine("Alpha"));
        Directory.CreateDirectory(temp.Combine(".hidden"));
        File.WriteAllText(temp.Combine("file.txt"), "x");

        FolderListing listing = await browser.List(temp.Path, CancellationToken.None);

        Assert.Equal(["Alpha", "beta"], listing.Folders.Select(folder => folder.Name));
        Assert.Equal(Directory.GetParent(temp.Path)!.FullName, listing.Parent);
    }

    [Fact]
    public async Task List_WithoutPathShowsHomeFolder()
    {
        FolderListing listing = await browser.List(null, CancellationToken.None);

        Assert.True(Directory.Exists(listing.Path));
    }

    [Fact]
    public async Task List_WithoutPathShowsConfiguredDefaultFolder()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("inside"));
        browser.Configure(temp.Path);

        FolderListing listing = await browser.List(null, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(temp.Path), listing.Path);
        Assert.Equal(["inside"], listing.Folders.Select(folder => folder.Name));
    }

    [Fact]
    public async Task List_RootHasNoParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Null((await browser.List("/", CancellationToken.None)).Parent);
    }

    [Fact]
    public async Task List_MissingFolderIsRejected()
    {
        using TempFolder temp = new();

        await Assert.ThrowsAsync<RequestException>(() => browser.List(temp.Combine("nope"), CancellationToken.None));
    }

    [Fact]
    public async Task Create_MakesFolderAndListsIt()
    {
        using TempFolder temp = new();

        FolderListing listing = await browser.Create(temp.Path, " fresh ", CancellationToken.None);

        Assert.Equal(temp.Combine("fresh"), listing.Path);
        Assert.True(Directory.Exists(temp.Combine("fresh")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task Create_RejectsBadNames(string name)
    {
        using TempFolder temp = new();

        await Assert.ThrowsAsync<RequestException>(() => browser.Create(temp.Path, name, CancellationToken.None));
    }

    [Fact]
    public async Task Create_RejectsExistingName()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("taken"));

        await Assert.ThrowsAsync<RequestException>(() => browser.Create(temp.Path, "taken", CancellationToken.None));
    }

    [Fact]
    public async Task Rename_MovesFolder()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("old"));
        File.WriteAllText(Path.Combine(temp.Combine("old"), "f.txt"), "x");

        string renamed = await browser.Rename(temp.Combine("old"), "new", CancellationToken.None);

        Assert.Equal(temp.Combine("new"), renamed);
        Assert.True(File.Exists(Path.Combine(renamed, "f.txt")));
        Assert.False(Directory.Exists(temp.Combine("old")));
    }

    [Fact]
    public async Task Rename_RejectsExistingTarget()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("a"));
        Directory.CreateDirectory(temp.Combine("b"));

        await Assert.ThrowsAsync<RequestException>(() => browser.Rename(temp.Combine("a"), "b", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_RemovesFolderWithContentsAndToleratesMissing()
    {
        using TempFolder temp = new();
        Directory.CreateDirectory(temp.Combine("doomed/inner"));

        await browser.Delete(temp.Combine("doomed"), CancellationToken.None);
        await browser.Delete(temp.Combine("doomed"), CancellationToken.None);

        Assert.False(Directory.Exists(temp.Combine("doomed")));
    }

    [Fact]
    public async Task Delete_RefusesRootAndHomeAndAncestors()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        await Assert.ThrowsAsync<RequestException>(() => browser.Delete(Path.GetPathRoot(home)!, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => browser.Delete(home, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => browser.Delete(Path.GetDirectoryName(home)!, CancellationToken.None));
    }
}
