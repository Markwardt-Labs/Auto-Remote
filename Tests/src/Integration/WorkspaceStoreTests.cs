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
        WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app", GroupId = group.Id, Service = AiService.Codex, AiSessionId = Guid.NewGuid().ToString("N") };

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
    public async Task Load_IgnoresTheModelAndEffortAnOlderServerStoredWhichTheAiSessionKeepsNow()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        string session = Guid.NewGuid().ToString();
        await File.WriteAllTextAsync(temp.Combine("workspaces.json"), $$"""{ "groups": [], "workspaces": [ { "id": "{{id}}", "folder": "/work/app", "aiSessionId": "{{session}}", "service": "Codex", "model": "Terra", "effort": "XHigh" } ] }""");

        WorkspaceInfo loaded = (await store.Load(CancellationToken.None)).Workspaces.Single();

        Assert.Equal((AiService.Codex, session), (loaded.Service, loaded.AiSessionId));
    }

    [Fact]
    public async Task SaveAttachmentThenDeleteAttachments_KeepsTheFileUntilTheWorkspaceIsDeleted()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();

        string path = await store.SaveAttachment(id, "../pic.png", new byte[] { 1, 2 }, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.EndsWith("-pic.png", path);
        store.DeleteAttachments(id);
        Assert.False(File.Exists(path));
    }
}
