namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public async Task SaveThenLoad_RoundTripsWorkspacesAndGroups()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("data/workspaces.json"));
        WorkspaceGroup group = new() { Id = Guid.NewGuid(), Name = "Work" };
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", GroupId = group.Id, Model = AiModel.Opus, Effort = AiEffort.XHigh, AiSessionId = Guid.NewGuid() };

        await store.Save(new WorkspaceCatalog { Groups = [group], Workspaces = [workspace] }, CancellationToken.None);

        WorkspaceStore reopened = new();
        reopened.Open(temp.Combine("data/workspaces.json"));
        WorkspaceCatalog loaded = await reopened.Load(CancellationToken.None);

        Assert.Equal([workspace], loaded.Workspaces);
        Assert.Equal([group], loaded.Groups);
    }

    [Fact]
    public async Task Load_WithoutFileIsEmpty()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));

        WorkspaceCatalog loaded = await store.Load(CancellationToken.None);

        Assert.Empty(loaded.Workspaces);
        Assert.Empty(loaded.Groups);
    }

    [Fact]
    public async Task Load_InvalidFileThrowsInvalidData()
    {
        using TempFolder temp = new();
        File.WriteAllText(temp.Combine("workspaces.json"), "{ nope");
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => store.Load(CancellationToken.None));
    }

    [Fact]
    public async Task History_AppendsReadsAndDeletes()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        ChatMessage user = new() { Role = MessageRole.User, Text = "line one\nline two" };
        ChatMessage reply = new() { Role = MessageRole.Assistant, Text = "ok" };

        Assert.Empty(await store.ReadHistory(id, CancellationToken.None));
        Assert.Equal(0, await store.AppendHistory(id, user, CancellationToken.None));
        Assert.Equal(1, await store.AppendHistory(id, reply, CancellationToken.None));

        Assert.Equal([user, reply], await store.ReadHistory(id, CancellationToken.None));
        store.DeleteHistory(id);
        Assert.Empty(await store.ReadHistory(id, CancellationToken.None));
    }
}
