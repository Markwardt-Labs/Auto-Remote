namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class CommitScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task GenerateCommitMessage_AnswersShortlyFromAForkAndLeavesTheConversationAlone(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("autocommit", service);
        await File.WriteAllTextAsync(Path.Combine(workspace.Folder, "greeting.txt"), "hello world\n");
        int mark = server.Mark();

        string message = await server.Connection.GenerateCommitMessage(workspace.Id, CancellationToken.None);

        Assert.InRange(message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 1, 8);
        Assert.DoesNotContain('\n', message);
        Assert.DoesNotContain(server.Events().Skip(mark), item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.MessageAdded);
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));

        await server.Connection.GitCommit(workspace.Id, message, CancellationToken.None);

        Assert.False((await server.Connection.GetGitStatus(workspace.Id, CancellationToken.None)).HasChanges);
    }
}
