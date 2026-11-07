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
        StoredWorkspace workspace = new() { Id = Guid.NewGuid(), GroupId = group.Id, SessionId = Guid.NewGuid().ToString("N") };

        await store.Save(new StoredCatalog { Groups = [group], Workspaces = [workspace] }, CancellationToken.None);

        WorkspaceStore reopened = new();
        reopened.Open(temp.Combine("data/workspaces.json"));
        StoredCatalog loaded = await reopened.Load(CancellationToken.None);

        Assert.Equal([workspace], loaded.Workspaces);
        Assert.Equal([group], loaded.Groups);
    }

    [Fact]
    public async Task Load_WithoutFileIsEmpty()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));

        StoredCatalog loaded = await store.Load(CancellationToken.None);

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
    public async Task Save_StoresOnlyWhoTheWorkspaceIsItsSessionAndItsGroup()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        WorkspaceStore store = new();
        store.Open(path);
        string session = Guid.NewGuid().ToString("N");

        await store.Save(new StoredCatalog { Workspaces = [new StoredWorkspace { Id = Guid.NewGuid(), SessionId = session }] }, CancellationToken.None);

        JsonObject workspace = JsonNode.Parse(await File.ReadAllTextAsync(path))!["workspaces"]![0]!.AsObject();
        Assert.Equal(["id", "sessionId", "groupId"], workspace.Select(pair => pair.Key));
        Assert.Equal(session, workspace["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Save_KeepsTheAdoptMarkOfAWorkspaceWhoseSessionStillNeedsItAndLeavesItOutOtherwise()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        WorkspaceStore store = new();
        store.Open(path);
        string session = Guid.NewGuid().ToString("N");

        await store.Save(new StoredCatalog { Workspaces = [new StoredWorkspace { Id = Guid.NewGuid(), SessionId = session, Adopt = true }, new StoredWorkspace { Id = Guid.NewGuid(), SessionId = session }] }, CancellationToken.None);
        StoredCatalog loaded = await store.Load(CancellationToken.None);

        JsonArray workspaces = JsonNode.Parse(await File.ReadAllTextAsync(path))!["workspaces"]!.AsArray();
        Assert.Equal((true, false), (workspaces[0]!.AsObject().ContainsKey("adopt"), workspaces[1]!.AsObject().ContainsKey("adopt")));
        Assert.Equal((true, false), (loaded.Workspaces[0].Adopt, loaded.Workspaces[1].Adopt));
    }

    [Fact]
    public async Task Save_KeepsTheFolderAndProviderOfAWorkspaceThatHasNoSessionYet()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        WorkspaceStore store = new();
        store.Open(path);

        await store.Save(new StoredCatalog { Workspaces = [new StoredWorkspace { Id = Guid.NewGuid(), Folder = "/work/old", Service = AiService.Codex }] }, CancellationToken.None);
        StoredWorkspace loaded = (await store.Load(CancellationToken.None)).Workspaces.Single();

        Assert.Equal((null, "/work/old", AiService.Codex), (loaded.SessionId, loaded.Folder, loaded.Service));
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

    [Fact]
    public async Task Load_UpgradesAFileAnEarlierReleaseWroteAndKeepsTheOriginal()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        Guid group = Guid.NewGuid();
        string current = Guid.NewGuid().ToString("N");
        string legacy = Guid.NewGuid().ToString();
        string original = $$"""
            { "groups": [ { "id": "{{group}}", "name": "Work" } ], "workspaces": [
              { "id": "{{Guid.NewGuid()}}", "folder": "/work/app", "service": "OpenCode", "model": "Haiku", "effort": "XHigh", "aiSessionId": "{{current}}", "groupId": "{{group}}" },
              { "id": "{{Guid.NewGuid()}}", "folder": "/work/two", "service": "OpenCode", "model": "Luna", "aiSessionId": "{{legacy}}" },
              { "id": "{{Guid.NewGuid()}}", "folder": "/work/three" } ] }
            """;
        await File.WriteAllTextAsync(path, original);
        WorkspaceStore store = new();
        store.Open(path);

        StoredCatalog loaded = await store.Load(CancellationToken.None);

        StoredWorkspace first = loaded.Workspaces[0];
        Assert.Equal((current, group, null, null, true), (first.SessionId, first.GroupId, first.Folder, first.Service, first.Adopt));
        Assert.False(loaded.Workspaces[1].Adopt || loaded.Workspaces[2].Adopt);
        Assert.Equal((null, "/work/two", AiService.Local), (loaded.Workspaces[1].SessionId, loaded.Workspaces[1].Folder, loaded.Workspaces[1].Service));
        Assert.Equal((null, "/work/three"), (loaded.Workspaces[2].SessionId, loaded.Workspaces[2].Folder));
        JsonObject stored = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(3, stored["version"]!.GetValue<int>());
        Assert.All(stored["workspaces"]!.AsArray(), workspace => Assert.True(workspace!["model"] is null && workspace["effort"] is null && workspace["aiSessionId"] is null));
        Assert.Equal(original, await File.ReadAllTextAsync(path + ".v1.bak"));
    }

    [Fact]
    public async Task Load_UpgradesAFileOfTheLastReleaseThatKeptFoldersAndServicesBySessionToJustTheSession()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        string session = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(path, $$"""{ "version": 2, "groups": [], "workspaces": [ { "id": "{{Guid.NewGuid()}}", "folder": "/work/app", "service": "Codex", "aiSessionId": "{{session}}" } ] }""");
        WorkspaceStore store = new();
        store.Open(path);

        StoredWorkspace loaded = (await store.Load(CancellationToken.None)).Workspaces.Single();

        Assert.Equal((session, null, null, true), (loaded.SessionId, loaded.Folder, loaded.Service, loaded.Adopt));
        Assert.True(File.Exists(path + ".v2.bak"));
        Assert.False(File.Exists(path + ".v1.bak"));
    }

    [Fact]
    public async Task Save_StoresTheVersionAndTheFileReadsBackAsIs()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        WorkspaceStore store = new();
        store.Open(path);

        await store.Save(new StoredCatalog { Workspaces = [new StoredWorkspace { Id = Guid.NewGuid(), SessionId = Guid.NewGuid().ToString("N") }] }, CancellationToken.None);
        StoredCatalog loaded = await store.Load(CancellationToken.None);

        Assert.Equal(3, JsonNode.Parse(await File.ReadAllTextAsync(path))!["version"]!.GetValue<int>());
        Assert.Single(loaded.Workspaces);
        Assert.Empty(Directory.GetFiles(temp.Path, "*.bak"));
    }

    [Fact]
    public async Task Load_RefusesAFileANewerReleaseWrote()
    {
        using TempFolder temp = new();
        string path = temp.Combine("workspaces.json");
        await File.WriteAllTextAsync(path, """{ "version": 9, "groups": [], "workspaces": [] }""");
        WorkspaceStore store = new();
        store.Open(path);

        await Assert.ThrowsAsync<NewerFileException>(() => store.Load(CancellationToken.None));
    }
}
