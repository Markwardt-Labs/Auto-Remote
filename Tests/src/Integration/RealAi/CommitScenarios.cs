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
        await File.WriteAllTextAsync(Path.Combine(workspace.Folder, "limits.md"), "# Limits\n\nAttachments can now be up to 20 MB each, up from 2 MB, and the connection allows messages up to 40 MB so the largest attachment fits in base64.\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Folder, "README.md"), "# autocommit\n\nThe first message of a connection is limited to 4 KB until the key handshake is done.\n");
        int mark = server.Mark();

        string message = string.Empty;

        for (int round = 0; round < 3; round++)
        {
            message = await server.Connection.GenerateCommitMessage(workspace.Id, CancellationToken.None);
            Console.Error.WriteLine("COMMIT MESSAGE: " + message);
            Assert.InRange(message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length, 1, 8);
            Assert.DoesNotContain('\n', message);
        }

        Assert.DoesNotContain(server.Events().Skip(mark), item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.MessageAdded);
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));

        await server.Connection.GitCommit(workspace.Id, message, CancellationToken.None);

        Assert.False((await server.Connection.GetGitStatus(workspace.Id, CancellationToken.None)).HasChanges);
    }
}
