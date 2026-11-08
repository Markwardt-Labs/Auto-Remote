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
    public async Task Messages_AreAppendedInOrderReplacedWholeAndDeletedWithTheWorkspace()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        DateTimeOffset time = DateTimeOffset.UtcNow;

        Assert.Null(await store.LoadMessages(id, CancellationToken.None));

        await store.AppendMessage(id, new ChatMessage { Role = MessageRole.User, Text = "multi\nline \"quoted\"", Time = time }, CancellationToken.None);
        await store.AppendMessage(id, new ChatMessage { Role = MessageRole.Assistant, Text = "answer" }, CancellationToken.None);

        WorkspaceStore reopened = new();
        reopened.Open(temp.Combine("workspaces.json"));
        IReadOnlyList<ChatMessage> loaded = (await reopened.LoadMessages(id, CancellationToken.None))!;
        Assert.Equal([("multi\nline \"quoted\"", MessageRole.User, (DateTimeOffset?)time), ("answer", MessageRole.Assistant, null)], loaded.Select(message => (message.Text, message.Role, message.Time)).ToList());

        await store.SaveMessages(id, [new ChatMessage { Role = MessageRole.User, Text = "only" }], CancellationToken.None);
        Assert.Equal(["only"], (await store.LoadMessages(id, CancellationToken.None))!.Select(message => message.Text).ToList());

        store.DeleteMessages(id);
        Assert.Null(await store.LoadMessages(id, CancellationToken.None));
    }

    [Fact]
    public async Task AppendMessage_PurgesTheOldestMessagesOnceTheChatGrowsPastItsLimit()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new(10);
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();

        for (int number = 1; number <= 12; number++)
        {
            await store.AppendMessage(id, new ChatMessage { Role = MessageRole.User, Text = $"m{number}" }, CancellationToken.None);
        }

        Assert.Equal(12, (await store.LoadMessages(id, CancellationToken.None))!.Count);

        await store.AppendMessage(id, new ChatMessage { Role = MessageRole.User, Text = "m13" }, CancellationToken.None);

        Assert.Equal(Enumerable.Range(4, 10).Select(number => $"m{number}").ToList(), (await store.LoadMessages(id, CancellationToken.None))!.Select(message => message.Text).ToList());
    }

    [Fact]
    public async Task AppendMessage_PurgesAnAlreadyOversizedChatAfterARestartOfTheServer()
    {
        using TempFolder temp = new();
        WorkspaceStore big = new(1000);
        big.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        await big.SaveMessages(id, [.. Enumerable.Range(1, 30).Select(number => new ChatMessage { Role = MessageRole.User, Text = $"m{number}" })], CancellationToken.None);
        WorkspaceStore small = new(10);
        small.Open(temp.Combine("workspaces.json"));

        await small.AppendMessage(id, new ChatMessage { Role = MessageRole.Assistant, Text = "m31" }, CancellationToken.None);

        Assert.Equal(Enumerable.Range(22, 10).Select(number => $"m{number}").ToList(), (await small.LoadMessages(id, CancellationToken.None))!.Select(message => message.Text).ToList());
    }

    [Fact]
    public async Task SaveMessages_KeepsOnlyTheNewestMessagesThatFitTheLimits()
    {
        using TempFolder temp = new();
        WorkspaceStore counted = new(5);
        counted.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        List<ChatMessage> all = [.. Enumerable.Range(1, 8).Select(number => new ChatMessage { Role = MessageRole.User, Text = $"m{number}" })];

        await counted.SaveMessages(id, all, CancellationToken.None);

        Assert.Equal(["m4", "m5", "m6", "m7", "m8"], (await counted.LoadMessages(id, CancellationToken.None))!.Select(message => message.Text).ToList());

        WorkspaceStore sized = new(100, 150);
        sized.Open(temp.Combine("workspaces.json"));
        await sized.SaveMessages(id, all, CancellationToken.None);

        IReadOnlyList<ChatMessage> kept = (await sized.LoadMessages(id, CancellationToken.None))!;
        Assert.InRange(kept.Count, 1, 7);
        Assert.Equal("m8", kept[^1].Text);
        Assert.True(new FileInfo(temp.Combine($"chats/{id:N}.jsonl")).Length <= 150);

        await sized.SaveMessages(id, [new ChatMessage { Role = MessageRole.Assistant, Text = new string('x', 500) }], CancellationToken.None);
        Assert.Single((await sized.LoadMessages(id, CancellationToken.None))!);
    }

    [Fact]
    public async Task LoadMessages_SkipsALineThatIsNotAMessage()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        await store.AppendMessage(id, new ChatMessage { Role = MessageRole.User, Text = "kept" }, CancellationToken.None);
        File.AppendAllText(temp.Combine($"chats/{id:N}.jsonl"), "{broken\n");
        await store.AppendMessage(id, new ChatMessage { Role = MessageRole.Assistant, Text = "also kept" }, CancellationToken.None);

        Assert.Equal(["kept", "also kept"], (await store.LoadMessages(id, CancellationToken.None))!.Select(message => message.Text).ToList());
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

    [Fact]
    public async Task Inputs_AreSavedPerWorkspaceReadBackAndDeletedWithIt()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        SavedInput first = new() { Id = Guid.NewGuid(), Text = "multi\nline \"quoted\" 日本語", Time = DateTimeOffset.UtcNow };

        Assert.Empty(await store.LoadInputs(id, CancellationToken.None));

        await store.SaveInputs(id, [first], CancellationToken.None);

        WorkspaceStore reopened = new();
        reopened.Open(temp.Combine("workspaces.json"));
        Assert.Equal([first], await reopened.LoadInputs(id, CancellationToken.None));
        Assert.Empty(await reopened.LoadInputs(Guid.NewGuid(), CancellationToken.None));

        await store.SaveInputs(id, [], CancellationToken.None);
        Assert.False(File.Exists(temp.Combine($"inputs/{id:N}.json")));

        await store.SaveInputs(id, [first], CancellationToken.None);
        store.DeleteInputs(id);
        Assert.Empty(await store.LoadInputs(id, CancellationToken.None));
    }

    [Fact]
    public async Task LoadInputs_IsEmptyForAFileThatCannotBeRead()
    {
        using TempFolder temp = new();
        WorkspaceStore store = new();
        store.Open(temp.Combine("workspaces.json"));
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(temp.Combine("inputs"));
        File.WriteAllText(temp.Combine($"inputs/{id:N}.json"), "{broken");

        Assert.Empty(await store.LoadInputs(id, CancellationToken.None));
    }
}
