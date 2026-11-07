namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class WorkScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task AttachedImagesAreSeenAndAttachedTextFilesAreReadByPath(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("attach", service);
        string image = await server.Connection.UploadAttachment(workspace.Id, "square.png", PngImage.Solid(48, 48, 255, 0, 0), CancellationToken.None);
        string notes = await server.Connection.UploadAttachment(workspace.Id, "notes.txt", Encoding.UTF8.GetBytes("The secret number is 4711."), CancellationToken.None);

        Assert.Contains("red", await server.Ask(workspace, $"What color is the square in the attached image? Answer with one lowercase word.\nAttached file: {image}"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4711", await server.Ask(workspace, $"What is the secret number in the attached text file? Answer with just the number.\nAttached file: {notes}"), StringComparison.Ordinal);
        ChatHistory history = await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Contains(history.Messages, message => message.Role == MessageRole.User && message.Text.Contains("Attached file: " + notes, StringComparison.Ordinal));
        Assert.Empty(server.Failures(workspace));
    }

    [RealAiFact]
    public async Task TwoWorkspacesWorkAtTheSameTimeWithoutMixingTheirConversations()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo claude = await server.Workspace("both-claude", AiService.ClaudeCode);
        WorkspaceInfo codex = await server.Workspace("both-codex", AiService.Codex);
        int mark = server.Mark();

        await Task.WhenAll(server.Connection.Prompt(claude.Id, "Run the shell command `sleep 3`, then remember the code word ALPHA and reply with just OK.", CancellationToken.None), server.Connection.Prompt(codex.Id, "Run the shell command `sleep 3`, then remember the code word BRAVO and reply with just OK.", CancellationToken.None));
        await Task.WhenAll(server.Idle(claude, mark), server.Idle(codex, mark));

        List<RemoteEvent> events = server.Events().Skip(mark).ToList();
        int firstEnd = events.FindIndex(item => item.Kind == RemoteEventKind.WorkingChanged && item.Working == false);
        Assert.Contains(events.Take(firstEnd), item => item.WorkspaceId == claude.Id && item.Kind == RemoteEventKind.WorkingChanged && item.Working == true);
        Assert.Contains(events.Take(firstEnd), item => item.WorkspaceId == codex.Id && item.Kind == RemoteEventKind.WorkingChanged && item.Working == true);
        Assert.Contains("ALPHA", await server.Ask(claude, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BRAVO", await server.Ask(codex, "What was the code word? Reply with just the word."), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BRAVO", server.Replies(claude, mark), StringComparison.Ordinal);
        Assert.DoesNotContain("ALPHA", server.Replies(codex, mark), StringComparison.Ordinal);
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task AToolThatCannotBeStartedIsReportedAsAFailureAndTheWorkspaceReturnsToReady(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        await server.Environment(environment => environment with { ClaudeCommand = "/nonexistent/claude", CodexCommand = "/nonexistent/codex" });
        WorkspaceInfo workspace = await server.Workspace("missing", service, configure: false);
        int mark = server.Mark();

        await server.Connection.Prompt(workspace.Id, "hello", CancellationToken.None);
        await server.Until(() => server.Failures(workspace, mark).Count > 0, 60, "the failure");
        await server.Idle(workspace, mark, 60);

        Assert.Contains("could not be started", server.Failures(workspace, mark)[0]);
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.False((await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Working);
    }

    [LocalModelFact]
    public async Task ALocalWorkspaceRunsAPromptThroughOpenCodeAndOllamaAndEditsFiles()
    {
        await using RealServer server = await RealServer.Start();
        string model = LocalModelFactAttribute.Model!;
        await server.Environment(environment => environment with { LocalModels = new CapabilityNames { Fast = model, Balanced = model, Strong = model, Max = model } });
        WorkspaceInfo workspace = await server.Workspace("local", AiService.Local);

        string reply = await server.Ask(workspace, "Create a file named hello.txt containing exactly the word banana, then tell me you did.", 900);

        Assert.Equal("banana", (await File.ReadAllTextAsync(Path.Combine(workspace.Folder, "hello.txt"))).Trim());
        Assert.False(string.IsNullOrWhiteSpace(reply));
        Assert.Empty(server.Failures(workspace));
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Matches("^[0-9a-f]{32}$", (await server.Connection.ListWorkspaces(CancellationToken.None)).Workspaces.Single().SessionId);
        ChatHistory history = await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Contains(history.Messages, message => message.Role == MessageRole.User && message.Text.Contains("hello.txt", StringComparison.Ordinal));
    }

    [RealAiFact]
    public async Task ALocalWorkspaceWhoseToolCannotBeStartedIsReportedAsAFailureAndReturnsToReady()
    {
        await using RealServer server = await RealServer.Start();
        await server.Environment(environment => environment with { LocalCommand = "/nonexistent/opencode", LocalModels = new CapabilityNames { Fast = "ollama/gemma4:26b", Balanced = "ollama/gemma4:26b", Strong = "ollama/gemma4:26b", Max = "ollama/gemma4:26b" } });
        WorkspaceInfo workspace = await server.Workspace("local-missing", AiService.Local);
        int mark = server.Mark();

        Assert.Equal(AiService.Local, workspace.Service);
        await server.Connection.Prompt(workspace.Id, "hello", CancellationToken.None);
        await server.Until(() => server.Failures(workspace, mark).Count > 0, 60, "the failure");
        await server.Idle(workspace, mark, 60);

        Assert.Contains("could not be started", server.Failures(workspace, mark)[0]);
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
    }

    [RealAiFact]
    public async Task TheModelsOfTheLocalServerAreTheOnesItHasInstalled()
    {
        using HttpClient http = new();
        string[] installed;

        try
        {
            using JsonDocument tags = JsonDocument.Parse(await http.GetStringAsync("http://localhost:11434/api/tags"));
            installed = [.. tags.RootElement.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("name").GetString()!)];
        }
        catch (HttpRequestException)
        {
            installed = [];
        }

        await using RealServer server = await RealServer.Start();

        IReadOnlyList<string> models = await server.Connection.ListLocalModels(null, CancellationToken.None);

        Assert.All(models, model => Assert.Contains(model.Replace("ollama/", string.Empty, StringComparison.Ordinal), installed));
        Assert.True(models.Count <= installed.Length);
    }

    [RealAiFact]
    public async Task APromptForAWorkspaceWhoseFolderWasRemovedIsRefusedOnceTheSessionIsReopenedAndTheWorkspaceStaysReady()
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("removed", AiService.ClaudeCode);
        await server.Ask(workspace, "Reply with exactly the word OK.");
        Directory.Delete(workspace.Folder, true);
        await server.Stop();
        await server.Launch();

        RemoteException error = await Assert.ThrowsAsync<RemoteException>(() => server.Connection.Prompt(workspace.Id, "hello", CancellationToken.None));

        Console.Error.WriteLine("ERROR: " + error.Message);
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        RemoteException history = await Assert.ThrowsAsync<RemoteException>(() => server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None));
        Assert.Contains("does not exist", history.Message, StringComparison.Ordinal);
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task UsageIsReadFromTheServicesOwnReport(AiService service)
    {
        await using RealServer server = await RealServer.Start();

        UsageStatus status = await server.Connection.GetUsage(service, CancellationToken.None);

        Assert.Equal(service, status.Service);
        Assert.NotEmpty(status.Windows);
        Assert.All(status.Windows, window => Assert.InRange(window.PercentUsed, 0, 100));
        Assert.All(status.Windows, window => Assert.False(string.IsNullOrWhiteSpace(window.Name)));
    }

    [RealAiFact]
    public async Task RebasingAConflictingBranchHasTheAiResolveItAndPushTheResult()
    {
        await using RealServer server = await RealServer.Start();
        string origin = Path.Combine(server.Root, "origin.git");
        Directory.CreateDirectory(origin);
        RealServer.Git(origin, "init", "--bare", "-b", "main");
        string folder = Path.Combine(server.Root, "work", "conflict");
        Directory.CreateDirectory(folder);
        RealServer.Git(folder, "init", "-b", "main");
        RealServer.Git(folder, "config", "user.email", "tester@example.com");
        RealServer.Git(folder, "config", "user.name", "Tester");
        RealServer.Git(folder, "remote", "add", "origin", origin);
        await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), "title: base\nbody: unchanged\n");
        RealServer.Git(folder, "add", "-A");
        RealServer.Git(folder, "commit", "-m", "base");
        RealServer.Git(folder, "push", "-u", "origin", "main");
        RealServer.Git(folder, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), "title: feature\nbody: unchanged\n");
        RealServer.Git(folder, "commit", "-am", "feature title");
        RealServer.Git(folder, "push", "-u", "origin", "feature");
        RealServer.Git(folder, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(folder, "file.txt"), "title: main\nbody: unchanged\n");
        RealServer.Git(folder, "commit", "-am", "main title");
        RealServer.Git(folder, "push", "origin", "main");
        WorkspaceInfo workspace = await server.Workspace("conflict", AiService.ClaudeCode, repository: false, folder: folder);
        int mark = server.Mark();

        await server.Connection.GitRebase(workspace.Id, "feature", "main", null, CancellationToken.None);

        string result = RealServer.Git(folder, "show", "feature:file.txt");
        Console.Error.WriteLine("RESOLVED: " + result.Replace("\n", " / "));
        Assert.DoesNotContain("<<<<<<<", result, StringComparison.Ordinal);
        Assert.DoesNotContain(">>>>>>>", result, StringComparison.Ordinal);
        Assert.Contains("body: unchanged", result, StringComparison.Ordinal);
        Assert.Equal("main", RealServer.Git(folder, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(string.Empty, RealServer.Git(folder, "status", "--porcelain"));
        Assert.Equal(RealServer.Git(folder, "rev-parse", "main"), RealServer.Git(folder, "merge-base", "main", "feature"));
        Assert.Equal(RealServer.Git(folder, "rev-parse", "feature"), RealServer.Git(origin, "rev-parse", "feature"));
        Assert.Equal(WorkspaceState.Ready, await server.Connection.GetWorkspaceState(workspace.Id, CancellationToken.None));
        Assert.Contains(server.Events().Skip(mark), item => item.Kind == RemoteEventKind.MessageAdded && item.Message!.Role == MessageRole.User && item.Message.Text.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }
}
