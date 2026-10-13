namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class DraftStoreTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Guid first = Guid.NewGuid();

    private readonly Guid second = Guid.NewGuid();

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task Load_IsEmptyWithoutAFile()
    {
        DraftStore store = new(temp.Combine("drafts.json"));

        Assert.Equal(string.Empty, await store.Load(first, CancellationToken.None));
    }

    [Fact]
    public async Task Save_KeepsADraftPerWorkspaceAcrossInstances()
    {
        string path = temp.Combine("sub/drafts.json");
        await new DraftStore(path).Save(first, "one\ntwo", CancellationToken.None);
        await new DraftStore(path).Save(second, "other", CancellationToken.None);

        DraftStore reopened = new(path);

        Assert.Equal("one\ntwo", await reopened.Load(first, CancellationToken.None));
        Assert.Equal("other", await reopened.Load(second, CancellationToken.None));
    }

    [Fact]
    public async Task Save_WithEmptyTextRemovesTheDraft()
    {
        DraftStore store = new(temp.Combine("drafts.json"));
        await store.Save(first, "one", CancellationToken.None);
        await store.Save(second, "two", CancellationToken.None);

        await store.Save(first, string.Empty, CancellationToken.None);

        Assert.Equal(string.Empty, await store.Load(first, CancellationToken.None));
        Assert.Equal("two", await store.Load(second, CancellationToken.None));
    }

    [Fact]
    public async Task Load_IgnoresAFileThatIsNotValid()
    {
        string path = temp.Combine("drafts.json");
        await File.WriteAllTextAsync(path, "{ not json");
        DraftStore store = new(path);

        Assert.Equal(string.Empty, await store.Load(first, CancellationToken.None));
        await store.Save(first, "fresh", CancellationToken.None);
        Assert.Equal("fresh", await store.Load(first, CancellationToken.None));
    }
}
