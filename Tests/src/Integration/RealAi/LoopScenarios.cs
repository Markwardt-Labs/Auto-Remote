namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class LoopScenarios
{
    private LoopSettings Forever(string prompt) => new() { Prompt = prompt, Condition = LoopCondition.Forever };

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task ALoopSendsItsPromptAsOftenAsItsConditionAllowsAndThenEndsByItself(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("loop", service);
        int mark = server.Mark();

        await server.Connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "Reply with just the word PING.", Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await server.Until(() => server.Events().Skip(mark).Any(item => item.Kind == RemoteEventKind.LoopChanged && item.Loop is null), 240, "the loop to end");
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Ready, 60, "the workspace to be ready");

        List<RemoteEvent> events = server.Events().Skip(mark).Where(item => item.WorkspaceId == workspace.Id).ToList();
        Assert.Equal(2, events.Count(item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.User && item.Message.Text == "Reply with just the word PING."));
        Assert.Equal(2, events.Count(item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.Assistant && item.Message.Text.Contains("PING", StringComparison.Ordinal)));
        Assert.Equal("The loop made all its runs.", events.Last(item => item.Kind == RemoteEventKind.LoopChanged).Text);
        Assert.Null(await server.Connection.GetLoop(workspace.Id, CancellationToken.None));
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task StoppingTheWorkDuringALoopStopsTheRunAndCancelsTheLoopSoNothingFollows(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("loop-stop", service);
        int mark = server.Mark();

        await server.Connection.StartLoop(workspace.Id, Forever("Run the shell command `sleep 271` and then reply with just OK."), CancellationToken.None);
        await server.Until(() => RealServer.Processes("sleep 271") > 0, 120, "the first run's command to run");
        await server.Connection.StopWorkspace(workspace.Id, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(8));

        Assert.Null(await server.Connection.GetLoop(workspace.Id, CancellationToken.None));
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Equal(1, server.Events().Skip(mark).Count(item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.User));
        await server.Until(() => RealServer.Processes("sleep 271") == 0, 20, "the long command to end");
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task CancellingTheLoopLetsTheRunInProgressFinishAndStartsNoOtherRun(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("loop-cancel", service);
        int mark = server.Mark();

        await server.Connection.StartLoop(workspace.Id, Forever("Run the shell command `sleep 8` and then reply with just FINISHED."), CancellationToken.None);
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Working, 90, "the first run to start");
        await server.Connection.CancelLoop(workspace.Id, CancellationToken.None);

        Assert.Null(await server.Connection.GetLoop(workspace.Id, CancellationToken.None));
        await server.Until(() => server.Replies(workspace, mark).Contains("FINISHED", StringComparison.Ordinal), 180, "the run to finish");
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Ready, 60, "the workspace to be ready");
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.Equal(1, server.Events().Skip(mark).Count(item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.User));
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task APromptSentDuringARunOfTheLoopIsWorkedOnAndTheLoopStillMakesAllItsRuns(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("loop-input", service);
        int mark = server.Mark();

        await server.Connection.StartLoop(workspace.Id, new LoopSettings { Prompt = "Run the shell command `sleep 4` and then reply with just LOOPRUN.", Condition = LoopCondition.Runs, Runs = 2 }, CancellationToken.None);
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Working, 90, "the first run to start");
        await server.Connection.Prompt(workspace.Id, "Create a file named between.txt containing exactly the word yes, then reply with just BETWEEN.", CancellationToken.None);
        await server.Until(() => server.Events().Skip(mark).Any(item => item.Kind == RemoteEventKind.LoopChanged && item.Loop is null) && server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Ready, 360, "the loop and the prompt to finish");

        string replies = server.Replies(workspace, mark);
        Assert.Equal("yes", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "between.txt"))).Trim());
        Assert.Equal(2, replies.Split("LOOPRUN").Length - 1);
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task APromptSentWhileACommandRunsIsAlwaysWorkedOnEvenWhenTheWorkEndsRightAfter(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("late-input", service, repository: false);

        foreach (int offset in new[] { 2000, 3000 })
        {
            string file = $"late{offset}.txt";
            await server.Connection.Prompt(workspace.Id, "Run the shell command `sleep 2` and then reply with just OK.", CancellationToken.None);
            await Task.Delay(offset);
            await server.Connection.Prompt(workspace.Id, $"Create a file named {file} containing exactly the word x, then reply with just DONE.", CancellationToken.None);
            await server.Until(() => File.Exists(Path.Combine(workspace.Folder, file)) && server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Ready, 240, $"the file {file}");
        }

        Assert.Empty(server.Failures(workspace));
    }
}
