namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private Task running = Task.CompletedTask;

    private IServerConnection connection = null!;

    private readonly List<RemoteEvent> received = [];

    private ConnectionSettings settings = null!;

    public async Task InitializeAsync()
    {
        string fake = temp.Combine("fake-claude.sh");
        File.WriteAllText(fake, "#!/bin/sh\nmode=new\nwhile [ $# -gt 0 ]; do case \"$1\" in --resume) mode=continued; sid=$2; shift;; --session-id) sid=$2; shift;; esac; shift; done\nif [ $mode = continued ] && [ ! -f \"$PWD/.ai-$sid\" ]; then printf '{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"errors\":[\"No conversation found with session ID: %s\"]}\\n' \"$sid\"; exit 1; fi\ntouch \"$PWD/.ai-$sid\"\nwhile IFS= read -r line; do\n  text=$(printf '%s' \"$line\" | sed 's/.*\"content\":\"\\([^\"]*\\)\".*/\\1/')\n  case \"$text\" in long*) sleep 1;; esac\n  printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"%s:%s:%s\"}]}}\\n' \"$mode\" \"$(basename \"$PWD\")\" \"$text\"\n  printf '{\"type\":\"result\",\"is_error\":false,\"result\":\"done\"}\\n'\ndone\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ServerConfig config = new() { Port = 0, Key = "secret", DataFolder = temp.Combine("data"), ClaudeCommand = fake, ClaudeArguments = [] };
        FolderBrowser folders = new();
        WorkspaceStore store = new();
        WorkspaceManager workspaces = new(store, folders);
        EventHub hub = new();
        GitService git = new();
        PromptService prompts = new(workspaces, store, new AiRunner(), hub);
        RequestHandler handler = new(workspaces, folders, new WorkspaceFileBrowser(), git, new WorkspaceVersioning(git, prompts, hub), prompts, new ScriptRunner(hub), Mock.Of<IUsageService>(), hub);
        await handler.Open(config, stopping.Token);
        ConnectionServer server = new(new Handshake(), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler, hub, Mock.Of<ILog>());
        TaskCompletionSource<int> listening = new();
        server.Listening += listening.SetResult;
        running = server.Run(config, stopping.Token);
        int port = await listening.Task;

        settings = new ConnectionSettings { Host = "127.0.0.1", Port = port, AccessKey = "secret" };
        connection = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings, CancellationToken.None);
        connection.EventReceived += remoteEvent =>
        {
            lock (received)
            {
                received.Add(remoteEvent);
            }
        };
    }

    private async Task<List<RemoteEvent>> Received(Func<List<RemoteEvent>, bool> done)
    {
        for (int attempt = 0; attempt < 250; attempt++)
        {
            lock (received)
            {
                if (done(received))
                {
                    return [.. received];
                }
            }

            await Task.Delay(20);
        }

        lock (received)
        {
            Assert.Fail("Timed out waiting for events: " + string.Join(", ", received.Select(remoteEvent => $"{remoteEvent.Kind}:{remoteEvent.Message?.Text}{remoteEvent.Working}{remoteEvent.Path}{remoteEvent.Error}")));
            return [];
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await connection.DisposeAsync();
        await stopping.CancelAsync();
        await running;
        temp.Dispose();
    }

    [Fact]
    public async Task Git_AllActionsRunOnTheServerThroughProtocolMessages()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        FolderListing app = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "gitapp", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, CancellationToken.None);

        Assert.False((await connection.GetGitStatus(workspace.Id, CancellationToken.None)).IsRepo);
        await connection.GitSetRemote(workspace.Id, "/nonexistent/app.git", CancellationToken.None);
        File.WriteAllText(Path.Combine(app.Path, "a.txt"), "one\ntwo\n");
        GitStatus fresh = await connection.GetGitStatus(workspace.Id, CancellationToken.None);
        Assert.True(fresh.IsRepo);
        Assert.True(fresh.HasChanges);
        Assert.Equal("/nonexistent/app.git", fresh.Remote);
        Assert.Equal([("a.txt", GitChangeStatus.Added)], (await connection.GetGitChanges(workspace.Id, CancellationToken.None)).Select(change => (change.Path, change.Status)));

        await connection.GitCommit(workspace.Id, "first", CancellationToken.None);
        File.WriteAllText(Path.Combine(app.Path, "a.txt"), "one\nTWO\n");
        Assert.Equal(GitChangeStatus.Modified, Assert.Single(await connection.GetGitChanges(workspace.Id, CancellationToken.None)).Status);
        GitDiff diff = await connection.GetGitDiff(workspace.Id, "a.txt", null, CancellationToken.None);
        Assert.Equal([GitDiffKind.Unchanged, GitDiffKind.Removed, GitDiffKind.Added], diff.Lines.Select(line => line.Kind));

        await connection.GitReset(workspace.Id, CancellationToken.None);
        Assert.False((await connection.GetGitStatus(workspace.Id, CancellationToken.None)).HasChanges);
        await connection.GitCreateBranch(workspace.Id, "feature", CancellationToken.None);
        await connection.GitCreateTag(workspace.Id, "v1", CancellationToken.None);
        GitStatus tagged = await connection.GetGitStatus(workspace.Id, CancellationToken.None);
        Assert.Equal(("feature", "v1", "first"), (tagged.Branch, tagged.Tag, tagged.Message));

        GitHistory history = await connection.GetGitHistory(workspace.Id, 0, 10, CancellationToken.None);
        GitCommit commit = Assert.Single(history.Commits);
        Assert.Contains(commit.Refs, item => item.Name == "v1" && item.Kind == GitRefKind.Tag);
        Assert.Equal([("a.txt", GitChangeStatus.Added)], (await connection.GetGitCommitChanges(workspace.Id, commit.Hash, CancellationToken.None)).Select(change => (change.Path, change.Status)));
        Assert.Equal(GitDiffKind.Added, Assert.Single((await connection.GetGitDiff(workspace.Id, "a.txt", commit.Hash, CancellationToken.None)).Lines, line => line.Text == "two").Kind);
        await Assert.ThrowsAsync<RemoteException>(() => connection.GetGitDiff(workspace.Id, "../x", null, CancellationToken.None));
        await Assert.ThrowsAsync<RemoteException>(() => connection.GitCreateBranch(workspace.Id, "feature", CancellationToken.None));
    }

    [Fact]
    public async Task CloneWorkspace_ClonesOnTheServerAndCreatesTheWorkspace()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        FolderListing source = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "origin", CancellationToken.None);
        WorkspaceInfo original = await connection.CreateWorkspace(source.Path, CancellationToken.None);
        await connection.GitSetRemote(original.Id, "/nonexistent/app.git", CancellationToken.None);
        File.WriteAllText(Path.Combine(source.Path, "a.txt"), "hello");
        await connection.GitCommit(original.Id, "first", CancellationToken.None);
        FolderListing parent = await connection.CreateFolder(temp.Path, "clones", CancellationToken.None);

        WorkspaceInfo cloned = await connection.CloneWorkspace(source.Path, parent.Path, "copy", CancellationToken.None);

        Assert.Equal("copy", cloned.Name);
        Assert.Equal("hello", (await connection.ReadFile(cloned.Id, "a.txt", CancellationToken.None)).Text);
        Assert.Equal("first", (await connection.GetGitStatus(cloned.Id, CancellationToken.None)).Message);
        await Assert.ThrowsAsync<RemoteException>(() => connection.CloneWorkspace(source.Path, parent.Path, "copy", CancellationToken.None));
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(settings with { AccessKey = "wrong" }, CancellationToken.None));
    }

    [Fact]
    public async Task WorkspaceLifecycle_CreateBrowseRenameDelete()
    {
        FolderListing parent = await connection.CreateFolder((await connection.ListFolders(temp.Path, CancellationToken.None)).Path, "projects", CancellationToken.None);
        FolderListing app = await connection.CreateFolder(parent.Path, "app", CancellationToken.None);

        WorkspaceInfo workspace = await connection.CreateWorkspace(app.Path, CancellationToken.None);
        Assert.Equal("app", workspace.Name);
        Assert.Equal([workspace], (await connection.ListWorkspaces(CancellationToken.None)).Workspaces);
        await Assert.ThrowsAsync<RemoteException>(() => connection.CreateWorkspace(app.Path, CancellationToken.None));

        File.WriteAllText(Path.Combine(app.Path, "notes.txt"), "remember");
        WorkspaceFiles files = await connection.ListFiles(workspace.Id, string.Empty, false, CancellationToken.None);
        Assert.Equal("notes.txt", Assert.Single(files.Entries).Path);
        Assert.Equal("remember", (await connection.ReadFile(workspace.Id, "notes.txt", CancellationToken.None)).Text);
        await Assert.ThrowsAsync<RemoteException>(() => connection.ListFiles(workspace.Id, "..", false, CancellationToken.None));

        WorkspaceInfo renamed = await connection.RenameWorkspace(workspace.Id, "app2", CancellationToken.None);
        Assert.Equal("app2", renamed.Name);
        Assert.True(File.Exists(Path.Combine(parent.Path, "app2", "notes.txt")));
        Assert.Equal(renamed, Assert.Single((await connection.ListWorkspaces(CancellationToken.None)).Workspaces));

        await connection.DeleteWorkspace(workspace.Id, false, CancellationToken.None);
        Assert.Empty((await connection.ListWorkspaces(CancellationToken.None)).Workspaces);
        Assert.True(Directory.Exists(renamed.Folder));

        WorkspaceInfo again = await connection.CreateWorkspace(renamed.Folder, CancellationToken.None);
        await connection.DeleteWorkspace(again.Id, true, CancellationToken.None);
        Assert.False(Directory.Exists(renamed.Folder));
    }

    [Fact]
    public async Task Prompt_StreamsTheConversationAsEventsAndResumesItsAiSessionInANewProcess()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "work", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, CancellationToken.None);

        await connection.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 2 && events.Last().Kind == RemoteEventKind.WorkingChanged);
        await connection.ConfigureWorkspace(workspace.Id, AiService.ClaudeCode, AiModel.Sonnet, AiEffort.Low, CancellationToken.None);
        await connection.Prompt(workspace.Id, "again", CancellationToken.None);
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && events.Last().Kind == RemoteEventKind.WorkingChanged);

        Assert.Equal(["hello", "new:work:hello", "again", "continued:work:again"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        Assert.Equal([0, 1, 2, 3], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Index));
        ChatHistory history = await connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Equal(["hello", "new:work:hello", "again", "continued:work:again"], history.Messages.Select(message => message.Text));
        Assert.False(history.Working);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksJoinsTheSameLiveSession()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "slow", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, CancellationToken.None);

        await connection.Prompt(workspace.Id, "long", CancellationToken.None);
        await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged && remoteEvent.Working == true));
        Assert.True((await connection.GetHistory(workspace.Id, null, null, CancellationToken.None)).Working);
        await connection.Prompt(workspace.Id, "short", CancellationToken.None);
        List<RemoteEvent> events = await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == 4 && events.Last().Working == false);

        Assert.Equal(["long", "short", "new:slow:long", "new:slow:short"], events.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
    }

    [Fact]
    public async Task History_IsReadInChunksFromTheNewestBackToTheBeginning()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "chunks", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, CancellationToken.None);

        for (int index = 0; index < 3; index++)
        {
            await connection.Prompt(workspace.Id, $"q{index}", CancellationToken.None);
            await Received(events => events.Count(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded) == (index + 1) * 2 && events.Last().Working == false);
        }

        ChatHistory newest = await connection.GetHistory(workspace.Id, null, 4, CancellationToken.None);
        ChatHistory older = await connection.GetHistory(workspace.Id, newest.Start, 4, CancellationToken.None);

        Assert.Equal(2, newest.Start);
        Assert.Equal(["q1", "new:chunks:q1", "q2", "new:chunks:q2"], newest.Messages.Select(message => message.Text));
        Assert.Equal(0, older.Start);
        Assert.Equal(["q0", "new:chunks:q0"], older.Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task WatchFiles_PushesAnEventWhenTheWatchedFolderChanges()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "watched", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(folder.Path, "src"));

        await connection.WatchFiles(workspace.Id, ["src"], CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(folder.Path, "src", "new.txt"), "x");

        List<RemoteEvent> events = await Received(events => events.Any(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged));
        RemoteEvent changed = events.First(remoteEvent => remoteEvent.Kind == RemoteEventKind.FilesChanged);
        Assert.Equal((workspace.Id, "src"), (changed.WorkspaceId, changed.Path));
        await Assert.ThrowsAsync<RemoteException>(() => connection.WatchFiles(workspace.Id, [".."], CancellationToken.None));
    }

    [Fact]
    public async Task Groups_OrganizeWorkspacesAndSettingsPersist()
    {
        FolderListing folder = await connection.CreateFolder(temp.Path, "organized", CancellationToken.None);
        WorkspaceInfo workspace = await connection.CreateWorkspace(folder.Path, CancellationToken.None);
        WorkspaceGroup group = await connection.CreateGroup("Clients", null, CancellationToken.None);

        await connection.MoveWorkspace(workspace.Id, group.Id, CancellationToken.None);
        await connection.ConfigureWorkspace(workspace.Id, AiService.ClaudeCode, AiModel.Haiku, AiEffort.Low, CancellationToken.None);
        WorkspaceCatalog catalog = await connection.ListWorkspaces(CancellationToken.None);

        Assert.Equal("Clients", Assert.Single(catalog.Groups).Name);
        WorkspaceInfo listed = Assert.Single(catalog.Workspaces);
        Assert.Equal(group.Id, listed.GroupId);
        Assert.Equal((AiModel.Haiku, AiEffort.Low), (listed.Model, listed.Effort));

        WorkspaceGroup nested = await connection.CreateGroup("Acme", group.Id, CancellationToken.None);
        WorkspaceGroup other = await connection.CreateGroup("Archive", null, CancellationToken.None);
        Assert.Equal(other.Id, (await connection.MoveGroup(group.Id, other.Id, CancellationToken.None)).ParentId);
        Assert.Equal(group.Id, (await connection.ListWorkspaces(CancellationToken.None)).Groups.Single(listedGroup => listedGroup.Id == nested.Id).ParentId);

        await connection.DeleteGroup(group.Id, CancellationToken.None);
        catalog = await connection.ListWorkspaces(CancellationToken.None);
        Assert.Equal(other.Id, Assert.Single(catalog.Workspaces).GroupId);
        Assert.Equal(other.Id, catalog.Groups.Single(listedGroup => listedGroup.Id == nested.Id).ParentId);
    }
}
