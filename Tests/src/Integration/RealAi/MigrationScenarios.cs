namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class MigrationScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task ConversationFromAnOlderServerIsAttachedRememberedAndKeptAfterARestart(AiService service)
    {
        string root = Path.Combine(Path.GetTempPath(), "realai-" + Guid.NewGuid().ToString("N")[..10]);
        string folder = Path.Combine(root, "work", "legacy");
        Directory.CreateDirectory(folder);
        RealServer.Git(folder, "init", "-b", "main");
        string legacy = await RealServer.LegacySession(service, folder, "Remember the code word MANGO. Reply with just OK.");
        Guid id = Guid.NewGuid();
        string model = service == AiService.Codex ? "Luna" : "Haiku";

        await using RealServer server = await RealServer.Start(root, prepare: async _ =>
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            await File.WriteAllTextAsync(Path.Combine(root, "data", "workspaces.json"), JsonSerializer.Serialize(new { groups = Array.Empty<object>(), workspaces = new[] { new { id, folder, aiSessionId = legacy, service = service.ToString(), model, effort = "XHigh" } } }));
        });
        WorkspaceInfo workspace = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single();
        server.Track(workspace);
        Assert.Equal((service, legacy), (workspace.Service, workspace.AiSessionId));
        WorkspaceAi chosen = await server.Connection.GetWorkspaceAi(workspace.Id, CancellationToken.None);
        Assert.Equal(service.DefaultModel(), chosen.Model);
        await server.Connection.ConfigureWorkspace(workspace.Id, service, RealServer.CheapModel(service), RealServer.Cheap(service), CancellationToken.None);

        ChatHistory before = await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Contains(before.Messages, message => message.Role == MessageRole.User && message.Text.Contains("MANGO", StringComparison.Ordinal));

        Assert.Contains("MANGO", await server.Ask(workspace, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
        string migrated = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId!;
        Assert.Matches("^[0-9a-f]{32}$", migrated);

        await server.Stop();
        await server.Launch();
        Assert.Equal(migrated, (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId);
        Assert.Contains("MANGO", await server.Ask(workspace, "Once more, what was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
        ChatHistory after = await server.Connection.GetHistory(workspace.Id, null, 50, CancellationToken.None);
        Assert.Contains(after.Messages, message => message.Role == MessageRole.User && message.Text.Contains("MANGO", StringComparison.Ordinal));
        Assert.Contains(after.Messages, message => message.Role == MessageRole.User && message.Text.StartsWith("Once more", StringComparison.Ordinal));
    }

    [RealAiFact]
    public async Task ASessionTheToolNoLongerHasStartsAFreshConversationInstead()
    {
        string root = Path.Combine(Path.GetTempPath(), "realai-" + Guid.NewGuid().ToString("N")[..10]);
        string folder = Path.Combine(root, "work", "gone");
        Directory.CreateDirectory(folder);
        Guid id = Guid.NewGuid();

        await using RealServer server = await RealServer.Start(root, prepare: async _ =>
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            await File.WriteAllTextAsync(Path.Combine(root, "data", "workspaces.json"), JsonSerializer.Serialize(new { groups = Array.Empty<object>(), workspaces = new[] { new { id, folder, aiSessionId = Guid.NewGuid().ToString(), service = "ClaudeCode", model = "Haiku", effort = "Low" } } }));
        });
        WorkspaceInfo workspace = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single();
        server.Track(workspace);

        Assert.Empty((await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Messages);
        Assert.Contains("OK", await server.Ask(workspace, "Reply with exactly the word OK."), StringComparison.Ordinal);
        Assert.Empty(server.Failures(workspace));
        Assert.Matches("^[0-9a-f]{32}$", (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId);
    }

    [RealAiFact]
    public async Task ChangingTheServiceStartsANewConversationInTheOtherTool()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("switch", AiService.ClaudeCode);
        await server.Ask(workspace, "Remember the code word KIWI. Reply with just OK.");
        string first = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId!;

        await server.Connection.ConfigureWorkspace(workspace.Id, AiService.Codex, RealServer.CheapModel(AiService.Codex), RealServer.Cheap(AiService.Codex), CancellationToken.None);

        string switched = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId!;
        Assert.NotEqual(first, switched);
        Assert.Empty((await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Messages);
        Assert.Equal(RealServer.CheapModel(AiService.Codex), (await server.Connection.GetWorkspaceAi(workspace.Id, CancellationToken.None)).Model);
        Assert.Contains("OK", await server.Ask(workspace, "Reply with exactly the word OK."), StringComparison.Ordinal);
        string second = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId!;
        Assert.NotEqual(first, second);
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, true)]
    [InlineData(AiService.ClaudeCode, false)]
    [InlineData(AiService.Codex, true)]
    public async Task DeletingAWorkspaceEndsItsSessionAndDeletesTheConversationOnlyWithTheFolder(AiService service, bool deleteFolder)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("deleting", service);
        await server.Ask(workspace, "Reply with exactly the word OK.");
        string live = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().AiSessionId!;
        string link = Path.Combine(server.Data, "liveai", "Sessions", live + ".json");
        Assert.True(File.Exists(link));
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(link));
        string provider = document.RootElement.EnumerateObject().First(property => property.Name.EndsWith("AiSessionId", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        string root = service == AiService.Codex ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        string[] Conversation() => Directory.GetFiles(root, $"*{provider}*.jsonl", SearchOption.AllDirectories);
        Assert.Single(Conversation());

        await server.Connection.DeleteWorkspace(workspace.Id, deleteFolder, CancellationToken.None);

        Assert.Equal(deleteFolder, !Directory.Exists(workspace.Folder));
        Assert.Equal(deleteFolder ? 0 : 1, Conversation().Length);
        Assert.Empty((await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces);

        foreach (string file in Conversation())
        {
            File.Delete(file);
        }
    }
}
