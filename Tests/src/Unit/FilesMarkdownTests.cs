namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class FilesMarkdownTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid id = Guid.NewGuid();

    private readonly Mock<IViewPreferences> preferences = new();

    private readonly FilesViewModel files;

    public FilesMarkdownTests()
    {
        GitViewModel git = new(connection.Object, id);
        connection.Setup(x => x.GetGitStatus(id, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStatus { IsRepo = false });
        connection.Setup(x => x.GetGitChanges(id, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        connection.Setup(x => x.ListFiles(id, string.Empty, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceFiles { Path = string.Empty, Entries = [Entry("code.cs"), Entry("doc.md"), Entry("other.md")] });
        connection.Setup(x => x.ReadFile(id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string path, CancellationToken _) => new FileContent { Path = path, Text = path.EndsWith(".md", StringComparison.Ordinal) ? "# Title\n\n## A" : "int x = 1;" });
        preferences.Setup(x => x.NumberedFiles(id, It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<string> { "other.md" });
        preferences.Setup(x => x.SetNumbered(id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        files = new FilesViewModel(connection.Object, id, git, true, TimeSpan.Zero, preferences.Object);
    }

    private FileEntry Entry(string path) => new() { Name = path, Path = path, IsFolder = false };

    private MarkdownHeading Heading(int index, int depth) => new() { Index = index, Title = $"H{index}", Level = depth + 1, Depth = depth, Number = $"{index}", Parent = depth - 1 };

    [Fact]
    public async Task MarkdownSource_IsTheTextOfAMarkdownFileAndNothingForOtherFilesSoCodeIsNeverRenderedAsMarkdown()
    {
        await files.Start();
        List<string?> changed = [];
        files.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        await files.Choose(files.Nodes.Single(node => node.Path == "code.cs"));
        string? code = files.MarkdownSource;
        await files.Choose(files.Nodes.Single(node => node.Path == "doc.md"));

        Assert.Null(code);
        Assert.Equal("# Title\n\n## A", files.MarkdownSource);
        Assert.Contains(nameof(FilesViewModel.MarkdownSource), changed);
    }

    [Fact]
    public void TheOutlineOffersAFoldLevelForEveryLevelOfNestingAndTellsWhenThereAreHeadings()
    {
        List<string?> changed = [];
        files.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.Equal((false, 0), (files.HasMarkdownOutline, files.MarkdownLevelChoices.Count));

        files.MarkdownOutline = [Heading(0, 0), Heading(1, 1), Heading(2, 2), Heading(3, 1)];

        Assert.True(files.HasMarkdownOutline);
        Assert.Equal([1, 2, 3], files.MarkdownLevelChoices);
        Assert.Contains(nameof(FilesViewModel.MarkdownLevelChoices), changed);
        Assert.Contains(nameof(FilesViewModel.HasMarkdownOutline), changed);
    }

    [Fact]
    public void TheBreadcrumbHasSomethingToShowOnlyWhileTheViewIsUnderAHeading()
    {
        List<string?> changed = [];
        files.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        Assert.False(files.HasMarkdownTrail);

        files.MarkdownTrail = [Heading(0, 0), Heading(1, 1)];
        Assert.True(files.HasMarkdownTrail);
        files.MarkdownTrail = [];

        Assert.False(files.HasMarkdownTrail);
        Assert.Contains(nameof(FilesViewModel.HasMarkdownTrail), changed);
    }

    [Fact]
    public void MarkdownNumbered_IsOffUntilTurnedOn()
    {
        Assert.False(files.MarkdownNumbered);

        files.MarkdownNumbered = true;

        Assert.True(files.MarkdownNumbered);
    }

    [Fact]
    public async Task MarkdownNumbered_IsOffForAFileUntilTheUserTurnsItOnAndThenItIsRememberedForThatFileOnly()
    {
        await files.Start();

        await files.Choose(files.Nodes.Single(node => node.Path == "doc.md"));
        Assert.False(files.MarkdownNumbered);
        preferences.Verify(x => x.SetNumbered(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        files.MarkdownNumbered = true;
        preferences.Verify(x => x.SetNumbered(id, "doc.md", true, It.IsAny<CancellationToken>()), Times.Once);

        await files.Choose(files.Nodes.Single(node => node.Path == "code.cs"));
        Assert.False(files.MarkdownNumbered);
        await files.Choose(files.Nodes.Single(node => node.Path == "doc.md"));
        Assert.True(files.MarkdownNumbered);
        preferences.Verify(x => x.SetNumbered(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MarkdownNumbered_StartsFromWhatWasRememberedForTheFileAndTurningItOffForgetsIt()
    {
        await files.Start();

        await files.Choose(files.Nodes.Single(node => node.Path == "other.md"));
        Assert.True(files.MarkdownNumbered);

        files.MarkdownNumbered = false;
        preferences.Verify(x => x.SetNumbered(id, "other.md", false, It.IsAny<CancellationToken>()), Times.Once);
        await files.Choose(files.Nodes.Single(node => node.Path == "doc.md"));
        await files.Choose(files.Nodes.Single(node => node.Path == "other.md"));

        Assert.False(files.MarkdownNumbered);
    }

    [Fact]
    public async Task MarkdownNumbered_KeepsWorkingWhenTheDeviceCannotStoreIt()
    {
        preferences.Setup(x => x.SetNumbered(id, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));
        await files.Start();
        await files.Choose(files.Nodes.Single(node => node.Path == "doc.md"));

        files.MarkdownNumbered = true;
        await Task.Delay(50);

        Assert.True(files.MarkdownNumbered);
    }
}
