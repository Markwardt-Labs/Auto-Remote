namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class SideSessionScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 473)]
    [InlineData(AiService.Codex, 474)]
    public async Task ASessionMadeWhileMainWorksKnowsWhatMainFinishedAndNothingOfTheWorkStillRunning(AiService service, int marker)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo main = await server.Workspace("fork", service);
        await server.Ask(main, "Remember the code word PINEAPPLE42. Reply with just OK.");
        int mark = server.Mark();
        await server.Connection.Prompt(main.Id, $"The second code word is MANGO99. Now run the shell command `sleep {marker}` and wait for it.", CancellationToken.None);
        await server.Until(() => RealServer.Processes($"sleep {marker}") > 0, 120, "main to start the long command");

        WorkspaceInfo side = await Side(server, main, "branch", SideSessionStart.Commit);

        Assert.True(RealServer.Processes($"sleep {marker}") > 0, "main's work was disturbed");
        Assert.Empty((await server.Connection.GetHistory(side.Id, null, null, null, CancellationToken.None)).Messages);
        string known = await server.Ask(side, "Without using any tools: list every code word you have been told in this conversation. If there are several, list them all.");
        Assert.Contains("PINEAPPLE42", known, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MANGO99", known, StringComparison.OrdinalIgnoreCase);
        ChatHistory chat = await server.Connection.GetHistory(side.Id, null, null, null, CancellationToken.None);
        Assert.Single(chat.Messages, message => message.Role == MessageRole.User);
        await server.Connection.StopWorkspace(main.Id, CancellationToken.None);
        await server.Idle(main, mark, 90);
        await server.Until(() => RealServer.Processes($"sleep {marker}") == 0, 30, "main's long command to end");
        await server.Connection.DeleteSideSession(side.Id, CancellationToken.None);
        await NothingIsLeft(server, main, side);
        Assert.Empty(server.Failures(side));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task IntegratingBringsBothSidesOfAConflictingChangeIntoMainWithoutCommittingAndRemovesTheSession(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo main = await server.Workspace("integrate", service, shell: false);
        await File.WriteAllTextAsync(Path.Combine(main.Folder, "greet.py"), "def greet(name):\n    return \"Hello \" + name\n");
        await File.WriteAllTextAsync(Path.Combine(main.Folder, "old.txt"), "no longer needed\n");
        RealServer.Git(main.Folder, "add", "-A");
        RealServer.Git(main.Folder, "commit", "-m", "greet");
        await server.Ask(main, "Reply with exactly the word OK.");
        WorkspaceInfo side = await Side(server, main, "excited", SideSessionStart.Files);

        await server.Ask(side, "Change greet.py so the greeting ends with an exclamation mark. Also create farewell.txt containing exactly the word Goodbye, and delete old.txt.", 420);
        await server.Ask(main, "In greet.py, change the word Hello to Howdy. Change nothing else.", 420);
        Assert.DoesNotContain("!", await File.ReadAllTextAsync(Path.Combine(main.Folder, "greet.py")), StringComparison.Ordinal);
        Assert.Contains("Hello", await File.ReadAllTextAsync(Path.Combine(side.Folder, "greet.py")), StringComparison.Ordinal);
        int mark = server.Mark();
        // Bringing in commits unless it is turned off.
        await server.Connection.SetBringInCommit(main.Id, side.Id, false, CancellationToken.None);

        await server.Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await Gone(server, side, 600);
        await Quiet(server, main);
        string greet = await File.ReadAllTextAsync(Path.Combine(main.Folder, "greet.py"));
        Assert.Contains("Howdy", greet, StringComparison.Ordinal);
        Assert.Contains("!", greet, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<<<<<", greet, StringComparison.Ordinal);
        Assert.Equal("Goodbye", (await File.ReadAllTextAsync(Path.Combine(main.Folder, "farewell.txt"))).Trim());
        Assert.False(File.Exists(Path.Combine(main.Folder, "old.txt")));
        Assert.Equal("2", RealServer.Git(main.Folder, "rev-list", "--count", "--all"));
        await NothingIsLeft(server, main, side);
        Assert.Empty(server.Failures(main, mark));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task ASessionSetToBringIntoMainIsBroughtInAndCommittedOnceItsWorkEndsWell(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo main = await server.Workspace("auto", service, shell: false);
        WorkspaceInfo side = await Side(server, main, "pear", SideSessionStart.Commit);
        await server.Connection.SetIntegrateWhenFinished(side.Id, true, CancellationToken.None);

        await server.Connection.Prompt(side.Id, "Create a file named notes.txt containing exactly the word pear. That is the whole task.", CancellationToken.None);

        await Gone(server, side, 600);
        await Quiet(server, main);
        Assert.Equal("pear", (await File.ReadAllTextAsync(Path.Combine(main.Folder, "notes.txt"))).Trim());
        await server.Until(() => RealServer.Git(main.Folder, "rev-list", "--count", "--all") == "2", 120, "the commit of what was brought in");
        await NothingIsLeft(server, main, side);
        Assert.Empty(server.Failures(main));
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 483)]
    [InlineData(AiService.Codex, 484)]
    public async Task ACancelledIntegrationIntoABusyMainKeepsTheSessionWhichCanBeIntegratedLater(AiService service, int marker)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo main = await server.Workspace("cancel", service);
        WorkspaceInfo side = await Side(server, main, "later", SideSessionStart.Commit);
        await server.Ask(side, "Create a file named later.txt containing exactly the word plum.", 420);
        int mark = server.Mark();
        await server.Connection.Prompt(main.Id, $"Run the shell command `sleep {marker}` and wait for it, then reply with just DONE.", CancellationToken.None);
        await server.Until(() => RealServer.Processes($"sleep {marker}") > 0, 120, "main to start the long command");

        await server.Connection.IntegrateSideSession(side.Id, CancellationToken.None);
        // It gets a turn of its own, so it waits while main works.
        await server.Until(() => server.Connection.GetAgenda(main.Id, CancellationToken.None).Result.Items.Any(item => item is { Kind: AgendaKind.BringIn, Waiting: null }), 180, "the request to wait for main");
        await server.Connection.CancelIntegration(side.Id, CancellationToken.None);

        Assert.Equal(SideSessionState.Ready, (await Find(server, side.Id))!.Side!.State);
        Assert.True(File.Exists(Path.Combine(side.Folder, "later.txt")));
        await server.Connection.StopWorkspace(main.Id, CancellationToken.None);
        await Quiet(server, main);
        Assert.False(File.Exists(Path.Combine(main.Folder, "later.txt")));
        Assert.NotNull(await Find(server, side.Id));

        await server.Connection.IntegrateSideSession(side.Id, CancellationToken.None);

        await Gone(server, side, 600);
        await Quiet(server, main);
        Assert.Equal("plum", (await File.ReadAllTextAsync(Path.Combine(main.Folder, "later.txt"))).Trim());
        await NothingIsLeft(server, main, side);
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode, 493)]
    [InlineData(AiService.Codex, 494)]
    public async Task DeletingASessionWhoseAiIsWorkingEndsItsCommandsAndLeavesNothingBehind(AiService service, int marker)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo main = await server.Workspace("delete", service);
        WorkspaceInfo side = await Side(server, main, "doomed", SideSessionStart.Files);
        await server.Connection.Prompt(side.Id, $"Run the shell command `sleep {marker}` and wait for it.", CancellationToken.None);
        await server.Until(() => RealServer.Processes($"sleep {marker}") > 0, 120, "the session to start the long command");

        await server.Connection.DeleteSideSession(side.Id, CancellationToken.None);

        await server.Until(() => RealServer.Processes($"sleep {marker}") == 0, 30, "the long command to end");
        Assert.Null(await Find(server, side.Id));
        await NothingIsLeft(server, main, side);
        Assert.Contains("OK", await server.Ask(main, "Reply with exactly the word OK."), StringComparison.Ordinal);
    }

    private async Task<WorkspaceInfo> Side(RealServer server, WorkspaceInfo main, string name, SideSessionStart start)
    {
        WorkspaceInfo made = await server.Connection.CreateSideSession(main.Id, name, start, null, false, CancellationToken.None);
        WorkspaceInfo? current = null;
        DateTime deadline = DateTime.UtcNow.AddSeconds(90);

        while ((current = await Find(server, made.Id)) is not { Side.State: SideSessionState.Ready })
        {
            Assert.True(DateTime.UtcNow < deadline, $"The session did not get ready: {current?.Side?.State} {current?.Side?.Error}");
            await Task.Delay(250);
        }

        Assert.Null(current.Side.Error);
        return current;
    }

    private async Task<WorkspaceInfo?> Find(RealServer server, Guid id) => (await server.Connection.ListWorkspaces(CancellationToken.None)).Sessions.FirstOrDefault(session => session.Id == id);

    private async Task Gone(RealServer server, WorkspaceInfo side, int seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);

        while (await Find(server, side.Id) is { } current)
        {
            if (DateTime.UtcNow > deadline || current.Side is { State: SideSessionState.Ready, Error: not null })
            {
                Assert.Fail($"The session was not integrated: {current.Side?.State} {current.Side?.Error}\n{string.Join("\n", server.Events().Where(item => item.Message is not null || item.Error is not null).TakeLast(30).Select(item => $"{(item.WorkspaceId == side.Id ? "side" : "main")} {item.Message?.Role} {item.Message?.Text} {item.Error}"))}");
            }

            await Task.Delay(500);
        }
    }

    // The AI goes on to say what it did after it finished an integration.
    private async Task Quiet(RealServer server, WorkspaceInfo workspace)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(240);
        int quiet = 0;

        while (quiet < 6)
        {
            Assert.True(DateTime.UtcNow < deadline, "The workspace did not come to rest.");
            quiet = await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None) == WorkspaceState.Ready ? quiet + 1 : 0;
            await Task.Delay(500);
        }
    }

    private async Task NothingIsLeft(RealServer server, WorkspaceInfo main, WorkspaceInfo side)
    {
        Assert.False(Directory.Exists(side.Folder), "the session's folder is still there");
        string kept = Path.Combine(server.Data, "sessions", main.Id.ToString("N"));
        Assert.True(!Directory.Exists(kept) || !Directory.EnumerateFileSystemEntries(kept).Any(), "files of the session are still there");
        Assert.Equal("main", RealServer.Git(main.Folder, "branch", "--format=%(refname:short)"));
        Assert.Empty(RealServer.Git(main.Folder, "for-each-ref", "--format=%(refname)", "refs/auto-remote"));
        Assert.Single(RealServer.Git(main.Folder, "worktree", "list").Split('\n'));

        // A conversation is the session's when the AI tool ran it in the session's folder.
        string marker = $"\"cwd\":\"{side.Folder}\"";
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        List<string> conversations = [];

        foreach (string store in new[] { Path.Combine(home, ".claude", "projects"), Path.Combine(home, ".codex", "sessions") }.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(store, "*.jsonl", SearchOption.AllDirectories).Where(file => File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-30)))
            {
                if ((await File.ReadAllTextAsync(file)).Contains(marker, StringComparison.Ordinal))
                {
                    conversations.Add(file);
                }
            }
        }

        Assert.Empty(conversations);
    }
}
