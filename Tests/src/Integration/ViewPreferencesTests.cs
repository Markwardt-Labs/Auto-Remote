namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ViewPreferencesTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Guid first = Guid.NewGuid();

    private readonly Guid second = Guid.NewGuid();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task NumberedFiles_IsEmptyWithoutAFile()
    {
        ViewPreferences store = new(temp.Combine("preferences.json"));

        Assert.Empty(await store.NumberedFiles(first, CancellationToken.None));
    }

    [Fact]
    public async Task SetNumbered_KeepsTheFilesPerWorkspaceAcrossInstances()
    {
        string path = temp.Combine("sub/preferences.json");
        await new ViewPreferences(path).SetNumbered(first, "docs/a.md", true, CancellationToken.None);
        await new ViewPreferences(path).SetNumbered(first, "b.md", true, CancellationToken.None);
        await new ViewPreferences(path).SetNumbered(second, "a.md", true, CancellationToken.None);

        ViewPreferences reopened = new(path);

        Assert.Equal(["b.md", "docs/a.md"], (await reopened.NumberedFiles(first, CancellationToken.None)).Order());
        Assert.Equal(["a.md"], await reopened.NumberedFiles(second, CancellationToken.None));
    }

    [Fact]
    public async Task SetNumbered_ForgetsAFileWhenItIsTurnedOffAndAWorkspaceWithNoFilesLeavesNothingBehind()
    {
        string path = temp.Combine("preferences.json");
        ViewPreferences store = new(path);
        await store.SetNumbered(first, "a.md", true, CancellationToken.None);
        await store.SetNumbered(first, "a.md", true, CancellationToken.None);

        await store.SetNumbered(first, "a.md", false, CancellationToken.None);
        await store.SetNumbered(first, "never.md", false, CancellationToken.None);

        Assert.Empty(await store.NumberedFiles(first, CancellationToken.None));
        Assert.DoesNotContain(first.ToString("N"), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task NumberedFiles_IgnoresAFileThatIsNotValid()
    {
        string path = temp.Combine("preferences.json");
        await File.WriteAllTextAsync(path, "not json");

        Assert.Empty(await new ViewPreferences(path).NumberedFiles(first, CancellationToken.None));
    }
}
