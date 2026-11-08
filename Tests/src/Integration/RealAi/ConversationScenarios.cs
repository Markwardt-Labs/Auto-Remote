namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class ConversationScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task Prompt_RunsCommandsAndEditsFilesInTheWorkspaceAndReportsTheWholeConversation(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("basic", service);

        string reply = await server.Ask(workspace, "Create a file named hello.txt containing exactly the word banana, then run the shell command `cat hello.txt` and tell me what it printed.");

        Assert.Equal("banana", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "hello.txt"))).Trim());
        Assert.Contains("banana", reply, StringComparison.OrdinalIgnoreCase);
        List<RemoteEvent> events = server.Events().Where(item => item.WorkspaceId == workspace.Id).ToList();
        Assert.Equal(MessageRole.User, events.First(item => item.Kind == RemoteEventKind.MessageAdded).Message!.Role);
        Assert.Equal([true, false], events.Where(item => item.Kind == RemoteEventKind.WorkingChanged).Select(item => item.Working == true).ToList());
        Assert.Empty(server.Failures(workspace));
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Matches("^[0-9a-f]{32}$", (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().SessionId);
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task Conversation_IsRememberedAcrossSettingChangesAndServerRestarts(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("memory", service);
        await server.Ask(workspace, "Remember the code word TANGERINE. Reply with just OK.");
        Assert.Contains("TANGERINE", await server.Ask(workspace, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);

        await server.Economize(workspace, AiEffort.Medium);
        Assert.Contains("TANGERINE", await server.Ask(workspace, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);

        await server.Stop();
        await server.Launch();

        ChatHistory history = await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Contains(history.Messages, message => message.Role == MessageRole.User && message.Text.Contains("TANGERINE", StringComparison.Ordinal));
        Assert.Contains("TANGERINE", await server.Ask(workspace, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task TheCapabilityAndEffortChosenInTheSettingsAreStoredByTheSessionOnceAPromptStartedAndComeBackAfterARestart(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("stored", service);
        await server.Ask(workspace, "Reply with just OK.");

        await server.Stop();
        await server.Launch();
        WorkspaceSettings settings = await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None);

        Assert.Equal((AiCapability.Fast, AiEffort.Low), (settings.Capability, settings.Effort));
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task AChoiceMadeBeforeAnyPromptSurvivesClosingTheSessionForARename(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("early", service);

        WorkspaceInfo renamed = await server.Connection.RenameWorkspace(workspace.Id, "later", CancellationToken.None);
        server.Track(renamed);
        WorkspaceSettings settings = await server.Connection.GetWorkspaceSettings(renamed.Id, CancellationToken.None);

        Assert.Equal((AiCapability.Fast, AiEffort.Low), (settings.Capability, settings.Effort));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task Rename_LetsTheNextPromptWorkInTheRenamedFolderInANewConversation(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("before", service);
        await server.Ask(workspace, "Reply with just OK.");

        WorkspaceInfo renamed = await server.Connection.RenameWorkspace(workspace.Id, "after", CancellationToken.None);
        server.Track(renamed);
        await server.Ask(renamed, "Create a file named here.txt containing the word yes. Reply with just OK.");

        Assert.Empty(server.Failures(renamed));
        Assert.Equal("yes", (await File.ReadAllTextAsync(Path.Combine(renamed.Folder, "here.txt"))).Trim());
        Assert.False(Directory.Exists(workspace.Folder));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task Prompt_SentWhileTheAiWorksIsWorkedIntoItsWorkInOneWorkingSpan(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("steer", service);
        await server.Connection.SetWorkspaceSettings(workspace.Id, (await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)) with { IsShellAllowed = true }, CancellationToken.None);
        int mark = server.Mark();

        await server.Connection.Prompt(workspace.Id, "Run the shell command `sleep 8`, and only after it finished create a file named note.txt containing exactly the word RED. Then reply with the word DONE.", CancellationToken.None);
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Working, 30, "the work to start");
        await server.Connection.Prompt(workspace.Id, "Correction: note.txt must contain exactly the word BLUE and not RED.", CancellationToken.None);
        await server.Idle(workspace, mark);

        Assert.Equal("BLUE", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "note.txt"))).Trim());
        Assert.Equal([true, false], server.Events().Skip(mark).Where(item => item.Kind == RemoteEventKind.WorkingChanged).Select(item => item.Working == true).ToList());
        Assert.Equal(2, server.Events().Skip(mark).Count(item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.User));
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    public async Task Prompt_SentAtAnyMomentWhileTheAiWorksIsAlwaysWorkedOnBeforeTheWorkspaceIsReadyAgain(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("late", service);
        int mark = server.Mark();

        await server.Connection.Prompt(workspace.Id, "Run the shell command `sleep 6`, then reply with the word DONE.", CancellationToken.None);
        await Task.Delay(2000);
        await server.Connection.Prompt(workspace.Id, "Also create a file named first.txt containing the word ONE.", CancellationToken.None);
        await Task.Delay(3000);
        await server.Connection.Prompt(workspace.Id, "Also create a file named second.txt containing the word TWO.", CancellationToken.None);
        await server.Idle(workspace, mark);

        Assert.Equal("ONE", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "first.txt"))).Trim());
        Assert.Equal("TWO", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "second.txt"))).Trim());
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task Prompt_SteeredIntoWorkIsWorkedOnOnceAndNeverReplayedAfterTheWorkEnds(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("replay", service);
        await server.Connection.SetWorkspaceSettings(workspace.Id, (await server.Connection.GetWorkspaceSettings(workspace.Id, CancellationToken.None)) with { IsShellAllowed = true }, CancellationToken.None);
        int mark = server.Mark();

        await server.Connection.Prompt(workspace.Id, "Run the shell command `sleep 20` and wait for it to finish. Then reply with the word DONE.", CancellationToken.None);
        await server.Until(() => server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None).Result == WorkspaceState.Working, 30, "the work to start");
        await Task.Delay(4000);
        await server.Connection.Prompt(workspace.Id, "Also create a file named steered.txt containing the word ONE.", CancellationToken.None);
        await Task.Delay(1500);
        await server.Connection.Prompt(workspace.Id, "Also create a file named second.txt containing the word TWO.", CancellationToken.None);
        await server.Idle(workspace, mark);
        await Task.Delay(3000);

        Assert.Equal("ONE", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "steered.txt"))).Trim());
        Assert.Equal("TWO", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "second.txt"))).Trim());
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Equal([true, false], server.Events().Skip(mark).Where(item => item.Kind == RemoteEventKind.WorkingChanged).Select(item => item.Working == true).ToList());
        Assert.DoesNotContain(server.Events().Skip(mark), item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.Assistant && item.Message.Text.Contains("already", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(server.Failures(workspace));
    }
}
