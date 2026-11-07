namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class MigrationScenarios
{
    [RealAiFact]
    public async Task ASessionTheToolNoLongerHasOrAnOlderServerWroteStartsAFreshConversationInstead()
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
        Assert.Matches("^[0-9a-f]{32}$", (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().SessionId);
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
        string live = (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().SessionId!;
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
