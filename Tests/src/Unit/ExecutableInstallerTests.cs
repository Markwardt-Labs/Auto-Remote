namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ExecutableInstallerTests : IDisposable
{
    private readonly TempFolder folder = new();

    private readonly Mock<IReleaseFeed> feed = new();

    private readonly ReleaseInfo release;

    private readonly string executable;

    public ExecutableInstallerTests()
    {
        executable = folder.Combine("app");
        File.WriteAllText(executable, "old");
        release = new ReleaseInfo { Version = new AppVersion { Major = 2, Minor = 0, Patch = 0 }, Assets = [new ReleaseAsset { Name = "asset", Url = "u", Size = 3 }] };
        feed.Setup(x => x.Download(It.IsAny<ReleaseAsset>(), It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>())).Returns((ReleaseAsset _, string path, IProgress<double>? _, CancellationToken _) => File.WriteAllTextAsync(path, "new"));
    }

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task Stage_DownloadsNextToTheExecutableAndChangesNothingUntilApplied()
    {
        ExecutableInstaller installer = new(feed.Object, executable, false);

        await installer.Stage(release, "asset", null, CancellationToken.None);

        Assert.Equal("old", File.ReadAllText(executable));
        Assert.Equal("new", File.ReadAllText($"{executable}.new"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode($"{executable}.new").HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public async Task Apply_PutsTheStagedFileInPlaceOfTheExecutable()
    {
        ExecutableInstaller installer = new(feed.Object, executable, false);
        await installer.Stage(release, "asset", null, CancellationToken.None);

        installer.Apply();

        Assert.Equal("new", File.ReadAllText(executable));
        Assert.Equal(["app"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Apply_OnWindowsKeepsTheRunningFileAsOldUntilItIsCleaned()
    {
        ExecutableInstaller installer = new(feed.Object, executable, true);
        await installer.Stage(release, "asset", null, CancellationToken.None);

        installer.Apply();

        Assert.Equal("new", File.ReadAllText(executable));
        Assert.Equal("old", File.ReadAllText($"{executable}.old"));

        installer.Clean();

        Assert.False(File.Exists($"{executable}.old"));
    }

    [Fact]
    public void Apply_FailsWhenNothingIsStaged()
    {
        ExecutableInstaller installer = new(feed.Object, executable, false);

        Assert.Throws<InvalidOperationException>(installer.Apply);
        Assert.Equal("old", File.ReadAllText(executable));
    }

    [Fact]
    public async Task Stage_FailsWhenTheReleaseHasNoSuchFileAndTouchesNothing()
    {
        ExecutableInstaller installer = new(feed.Object, executable, false);

        await Assert.ThrowsAsync<IOException>(() => installer.Stage(release, "missing", null, CancellationToken.None));

        Assert.Equal("old", File.ReadAllText(executable));
        feed.Verify(x => x.Download(It.IsAny<ReleaseAsset>(), It.IsAny<string>(), It.IsAny<IProgress<double>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Clean_DeletesAStagedFileThatWasNeverApplied()
    {
        ExecutableInstaller installer = new(feed.Object, executable, false);
        await installer.Stage(release, "asset", null, CancellationToken.None);

        installer.Clean();

        Assert.Equal(["app"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData(null)]
    public async Task Stage_RefusesWhenTheProgramDoesNotRunFromAnExecutable(string? path)
    {
        ExecutableInstaller installer = new(feed.Object, path, false);

        Assert.Null(installer.Executable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => installer.Stage(release, "asset", null, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(installer.Apply);
        installer.Clean();
    }

    [Fact]
    public void Factory_UsesTheRunningProcess() => Assert.NotNull(new IExecutableInstaller.Factory().Create(feed.Object));
}
