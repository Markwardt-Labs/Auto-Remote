namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class ControlScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 241)]
    [InlineData(AiService.Codex, 242)]
    public async Task Stop_EndsLongWorkAtOnceKeepsWhatItDidAndTheConversationContinues(AiService service, int marker)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("stop", service);
        int mark = server.Mark();
        await server.Connection.Prompt(workspace.Id, $"First create a file named partial.txt containing the word started. Then run the shell command `sleep {marker}` (it takes a while, wait for it). Then create done.txt.", CancellationToken.None);
        await server.Until(() => File.Exists(Path.Combine(workspace.Folder, "partial.txt")) && RealServer.Processes($"sleep {marker}") > 0, 120, "the AI to start the long command");

        DateTime started = DateTime.UtcNow;
        await server.Connection.StopWorkspace(workspace.Id, CancellationToken.None);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(40), "Stop took " + (DateTime.UtcNow - started));
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(workspace.Folder, "partial.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.Folder, "done.txt")));
        await server.Until(() => RealServer.Processes($"sleep {marker}") == 0, 20, "the long command to end");
        Assert.Empty(server.Failures(workspace, mark));
        Assert.False(server.Events().Skip(mark).Last(item => item.Kind == RemoteEventKind.WorkingChanged).Working);

        string reply = await server.Ask(workspace, "Which file did you create before I stopped you? Reply with just its name.");
        Assert.Contains("partial", reply, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(workspace.Folder, "done.txt")));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 261)]
    [InlineData(AiService.Codex, 262)]
    public async Task ShuttingTheServerDownDuringWorkEndsTheToolAndItsCommandsAndTheNextStartResumes(AiService service, int marker)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("shutdown", service);
        await server.Ask(workspace, "Remember the code word PAPAYA. Reply with just OK.");
        await server.Connection.Prompt(workspace.Id, $"Run the shell command `sleep {marker}` and wait for it.", CancellationToken.None);
        await server.Until(() => RealServer.Processes($"sleep {marker}") > 0, 120, "the AI to start the long command");

        await server.Stop();

        await server.Until(() => RealServer.Processes($"sleep {marker}") == 0, 30, "the long command to end with the server");
        await server.Launch();
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Contains("PAPAYA", await server.Ask(workspace, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
    }
}
