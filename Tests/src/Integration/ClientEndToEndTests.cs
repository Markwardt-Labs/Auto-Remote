namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class ClientEndToEndTests(HeadlessFixture fixture) : IClassFixture<HeadlessFixture>, IAsyncLifetime
{
    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly Dictionary<string, string?> savedEnvironment = [];

    private Task running = Task.CompletedTask;

    private ConnectionSettings settings = null!;

    private string origin = string.Empty;

    private string teammate = string.Empty;

    private string clones = string.Empty;

    private string aiDelay = string.Empty;

    public async Task InitializeAsync()
    {
        foreach (string name in new[] { "GIT_AUTHOR_NAME", "GIT_AUTHOR_EMAIL", "GIT_COMMITTER_NAME", "GIT_COMMITTER_EMAIL", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_SYSTEM" })
        {
            savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        string gitConfig = temp.Combine("gitconfig");
        File.WriteAllText(gitConfig, "[init]\n\tdefaultBranch = main\n[user]\n\tuseConfigOnly = true\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", "/dev/null");
        origin = temp.Combine("origin.git");
        teammate = temp.Combine("teammate");
        clones = temp.Combine("clones");
        aiDelay = temp.Combine("ai-delay");
        Directory.CreateDirectory(clones);
        File.WriteAllText(aiDelay, "0");
        G(temp.Path, "init", "--bare", "-b", "main", origin);
        G(temp.Path, "clone", origin, teammate);
        Directory.CreateDirectory(Path.Combine(teammate, "src"));
        File.WriteAllText(Path.Combine(teammate, "README.md"), "one\ntwo\nthree\n");
        File.WriteAllText(Path.Combine(teammate, "src", "App.cs"), "class App {}\n");
        File.WriteAllText(Path.Combine(teammate, "src", "Util.cs"), "class Util {}\n");
        File.WriteAllText(Path.Combine(teammate, ".gitignore"), "bin/\n");
        T("add", "-A");
        T("commit", "-m", "Initial commit");
        T("branch", "-M", "main");
        File.WriteAllText(Path.Combine(teammate, "src", "App.cs"), "class App { int a; }\n");
        T("commit", "-am", "Add a field");
        T("tag", "-a", "v0.1", "-m", "v0.1");
        T("push", "origin", "main", "--tags");
        T("checkout", "-b", "feature/ui");
        File.WriteAllText(Path.Combine(teammate, "ui.md"), "ui\n");
        T("add", "-A");
        T("commit", "-m", "Add UI docs");
        T("push", "origin", "feature/ui");
        T("checkout", "main");

        string fake = temp.Combine("fake-claude.sh");
        File.WriteAllText(fake, $$$"""
            #!/bin/sh
            while [ $# -gt 0 ]; do case "$1" in --resume) shift;; --session-id) shift;; esac; shift; done
            while IFS= read -r line; do
              text=$(printf '%s' "$line" | sed 's/.*"content":"\([^"]*\)".*/\1/')
              case "$text" in
                *"merge conflicts in"*)
                  sleep "$(cat {{{aiDelay}}})"
                  for f in $(git diff --name-only --diff-filter=U); do sed -i '/^<<<<<<< /d;/^=======$/d;/^>>>>>>> /d' "$f"; git add "$f"; done
                  reply="resolved conflicts";;
                *) reply="ok";;
              esac
              printf '{"type":"assistant","message":{"content":[{"type":"text","text":"%s"}]}}\n' "$reply"
              printf '{"type":"result","is_error":false,"result":"done"}\n'
            done
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        ServerConfig config = new() { Port = 0, WebPort = 0, Key = "secret", DataFolder = temp.Combine("data"), DefaultFolder = clones, ClaudeCommand = fake, ClaudeArguments = [] };
        FolderBrowser folders = new();
        WorkspaceStore store = new();
        WorkspaceManager workspaces = new(store, folders);
        EventHub hub = new();
        GitService git = new();
        PromptService prompts = new(workspaces, store, new AiRunner(), hub);
        RequestHandler handler = new(workspaces, folders, new WorkspaceFileBrowser(), git, new WorkspaceVersioning(git, prompts, hub), prompts, Mock.Of<IUsageService>(), hub);
        await handler.Open(config, stopping.Token);
        ConnectionServer server = new(new Handshake(), new IMessageChannel.Factory(), new WebSocketUpgrade(), new CertificateProvider(), handler, hub, Mock.Of<ILog>());
        TaskCompletionSource<int> listening = new();
        server.Listening += (tcp, _) => listening.SetResult(tcp);
        running = server.Run(config, stopping.Token);
        settings = new ConnectionSettings { Host = "127.0.0.1", Port = await listening.Task, AccessKey = "secret" };
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await stopping.CancelAsync();
        await running;

        foreach ((string name, string? value) in savedEnvironment)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        temp.Dispose();
    }

    private string G(string folder, params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_AUTHOR_NAME"] = "Team";
        start.Environment["GIT_AUTHOR_EMAIL"] = "team@example.com";
        start.Environment["GIT_COMMITTER_NAME"] = "Team";
        start.Environment["GIT_COMMITTER_EMAIL"] = "team@example.com";
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private string T(params string[] arguments) => G(teammate, arguments);

    private async Task Until(Func<bool> condition, string what, int milliseconds = 20000)
    {
        DateTime end = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail("Timed out waiting for: " + what);
    }

    private async Task Answer(GitViewModel git, string? text = null, string? email = null)
    {
        await Until(() => git.IsPrompting, "a question");

        if (text is not null)
        {
            git.PromptText = text;
        }

        if (email is not null)
        {
            git.PromptEmail = email;
        }

        git.ConfirmCommand.Execute(null);
    }

    private async Task Settled(GitViewModel git) => await Until(() => !git.IsPrompting && !git.IsWorking, "git to settle");

    private IEnumerable<string> Texts(Window window) => window.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text ?? string.Empty);

    private IEnumerable<string> Tabs(Window window) => window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("tab") && button.IsEffectivelyVisible).Select(button => button.Content?.ToString() ?? string.Empty);

    [Theory]
    [InlineData(ClientLayout.Panels)]
    [InlineData(ClientLayout.Tabs)]
    public async Task EveryGitFeatureWorksThroughTheRealClientAgainstARealServerAndRepositories(ClientLayout layout)
    {
        string app = Path.Combine(clones, "app");
        Mock<IAppInfo> info = new();
        info.SetupGet(x => x.Name).Returns("Auto Remote");
        info.SetupGet(x => x.Version).Returns("1.0.0");
        Mock<ISettingsStore> store = new();
        store.Setup(x => x.Load(It.IsAny<CancellationToken>())).ReturnsAsync(settings);
        MainViewModel main = new(info.Object, new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()), store.Object, layout);

        await fixture.RunAsync(async () =>
        {
            Window window = new() { Width = layout == ClientLayout.Panels ? 1400 : 420, Height = 850, Content = new MainView { DataContext = main } };
            window.Show();
            await main.Start();
            HomeViewModel home = Assert.IsType<HomeViewModel>(main.Current);

            // clone a workspace from the origin
            home.CloneWorkspaceCommand.Execute(null);
            await Until(() => main.Current is CloneWorkspaceViewModel { Browser.CanChoose: true }, "the clone screen");
            CloneWorkspaceViewModel cloning = (CloneWorkspaceViewModel)main.Current!;
            cloning.Url = origin;
            cloning.Name = "app";
            cloning.CloneCommand.Execute(null);
            await Until(() => main.Current is HomeViewModel { Active: not null }, "the cloned workspace");
            home = (HomeViewModel)main.Current!;
            WorkspaceViewModel workspace = home.Active!;
            GitViewModel git = workspace.Git;
            await Until(() => git.IsRepo && git.Commit.Length > 0, "the git status");
            Assert.True(Directory.Exists(Path.Combine(app, ".git")));
            Assert.Equal("main", git.Title);
            Assert.Equal("Add a field", git.Message);
            Assert.True(workspace.ShowHistoryTab);
            await Task.Delay(100);
            await ShowFiles(workspace, layout);
            Assert.Contains("main", Texts(window));
            Assert.Contains("Add a field", Texts(window));
            Assert.Equal(layout == ClientLayout.Tabs ? ["Chat", "Files", "History"] : ["Chat", "History"], Tabs(window).ToArray());

            // edits on the server color the tree, appear in changes mode and as a diff
            File.WriteAllText(Path.Combine(app, "README.md"), "one\nTWO\nthree\nfour\n");
            File.WriteAllText(Path.Combine(app, "src", "New.cs"), "new\n");
            File.Delete(Path.Combine(app, "src", "Util.cs"));
            Directory.CreateDirectory(Path.Combine(app, "bin"));
            File.WriteAllText(Path.Combine(app, "bin", "out.dll"), "x");
            await Until(() => workspace.Files.Nodes.Any(node => node.Name == "README.md" && node.IsModified), "README colored modified");
            Assert.True(workspace.Files.Nodes.Single(node => node.Name == "bin").IsIgnored);
            Assert.True(workspace.Files.Nodes.Single(node => node.Name == "src").IsModified);
            Assert.True(git.HasChanges);
            git.IsChangesMode = true;
            ChangeNode src = git.ChangeTree.Single(node => node.Name == "src");
            Assert.Equal(["New.cs", "Util.cs"], src.Children.Select(node => node.Name));
            Assert.True(src.Children[1].IsDeleted);
            Assert.DoesNotContain(git.ChangeTree, node => node.Name == "bin");
            git.ChangeTree.Single(node => node.Name == "README.md").OpenCommand.Execute(null);
            await Until(() => workspace.Files.IsDiffPreview, "the diff");
            Assert.Equal([GitDiffKind.Unchanged, GitDiffKind.Removed, GitDiffKind.Added, GitDiffKind.Unchanged, GitDiffKind.Added], workspace.Files.Diff!.Lines.Select(line => line.Kind));
            Assert.True(workspace.ShowContentPanel);
            File.WriteAllText(Path.Combine(app, "README.md"), "one\nTWO\nthree\nfour\nfive\n");
            await Until(() => workspace.Files.Diff!.Lines.Count(line => line.Kind == GitDiffKind.Added) == 3, "the diff following the edit");
            git.IsChangesMode = false;
            workspace.Files.ClosePreviewCommand.Execute(null);

            // commit asks for the identity first, then commits and pushes
            git.CommitCommand.Execute(null);
            Assert.Equal(GitPrompt.Identity, git.Prompt);
            await Answer(git, "Eve Tester", "eve@example.com");
            await Until(() => git.Prompt == GitPrompt.Input && git.PromptTitle == "Commit", "the commit question");
            await Answer(git, "Change README and files");
            await Settled(git);
            await Until(() => !git.HasChanges && git.Message == "Change README and files", "the commit");
            Assert.Equal("Change README and files", G(origin, "log", "-1", "--format=%s", "main"));
            Assert.Equal("Eve Tester", G(app, "log", "-1", "--format=%an", "main"));

            // branch and tag are pushed
            git.BranchCommand.Execute(null);
            await Answer(git, "feature/e2e");
            await Settled(git);
            await Until(() => git.Title == "feature/e2e", "the new branch");
            git.TagCommand.Execute(null);
            await Answer(git, "v1.0");
            await Settled(git);
            await Until(() => git.Tag == "v1.0", "the new tag");
            Assert.Contains("feature/e2e", G(origin, "branch", "--list", "feature/e2e"));
            Assert.Equal("v1.0", G(origin, "tag", "--list", "v1.0"));
            git.BranchCommand.Execute(null);
            await Answer(git, "feature/e2e");
            await Until(() => git.Error is not null && git.Error.Contains("already exists"), "the refusal");
            git.CancelCommand.Execute(null);

            // history with badges, commit changes and a commit diff
            await ShowHistory(workspace, layout);
            await Until(() => git.History.Commits.Count >= 4, "the history");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Change README and files");
            GitCommitViewModel newest = git.History.Commits[0];
            Assert.Contains(newest.Refs, item => item.Name == "v1.0" && item.IsTag);
            Assert.Contains(newest.Refs, item => item.Name == "feature/e2e");
            newest.ToggleCommand.Execute(null);
            await Until(() => newest.Changes.Count > 0, "the commit's files");
            newest.Changes.Single(node => node.Name == "README.md").OpenCommand.Execute(null);
            await Until(() => workspace.Files.IsDiffPreview && workspace.Files.PreviewTitle.Contains('('), "the commit diff");
            Assert.True(workspace.ShowContentPanel);
            workspace.Files.ClosePreviewCommand.Execute(null);

            // checkout of a branch discards pending changes after asking
            File.WriteAllText(Path.Combine(app, "README.md"), "dirty\n");
            await Until(() => git.HasChanges, "pending changes");
            GitRefViewModel mainBadge = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "main");
            Assert.True(mainBadge.CanCheckout);
            GitRefViewModel other = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "feature/e2e");
            Assert.False(other.CanCheckout);
            git.BranchCommand.Execute(null);
            await Answer(git, "side");
            await Settled(git);
            await Until(() => git.Title == "side", "side branch");
            await Until(() => git.History.Commits.SelectMany(commit => commit.Refs).Any(item => item.Name == "side"), "side in history");
            GitRefViewModel mainRef = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "main");
            mainRef.CheckoutCommand.Execute(null);
            await Task.Delay(100);
            Assert.Contains("discard your pending changes", git.PromptHint);
            await Answer(git);
            await Settled(git);
            await Until(() => git.Title == "main", "back on main");
            Assert.Equal(string.Empty, G(app, "status", "--porcelain"));
            Assert.Equal("one\nTWO\nthree\nfour\nfive", File.ReadAllText(Path.Combine(app, "README.md")).Replace("\r\n", "\n").TrimEnd());

            // a tag detaches, a branch returns
            GitRefViewModel tag = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "v0.1");
            tag.CheckoutCommand.Execute(null);
            await Task.Delay(100);
            await Answer(git);
            await Until(() => git.CurrentBranch is null && git.Title == "v0.1", "detached at the tag");
            mainRef = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "main");
            mainRef.CheckoutCommand.Execute(null);
            await Task.Delay(100);
            await Answer(git);
            await Until(() => git.Title == "main", "main again");

            // a teammate's push arrives through fetch and the automatic pull, around local changes
            T("pull", "origin", "main");
            File.WriteAllText(Path.Combine(teammate, "team.txt"), "t\n");
            T("add", "-A");
            T("commit", "-m", "Teammate commit");
            T("push", "origin", "main");
            git.FetchCommand.Execute(null);
            await Until(() => git.History.Commits.Any(commit => commit.Subject == "Teammate commit"), "the teammate commit after fetch");
            File.WriteAllText(Path.Combine(app, "mine.txt"), "mine\n");
            File.WriteAllText(Path.Combine(app, "README.md"), "one\nTWO\nthree\nfour\nfive\nmine\n");
            await Until(() => git.HasChanges, "local pending changes");
            await git.SyncOnce();
            await Until(() => File.Exists(Path.Combine(app, "team.txt")), "the pull");
            Assert.Contains("mine", File.ReadAllText(Path.Combine(app, "README.md")));
            Assert.Equal(string.Empty, G(app, "stash", "list"));
            git.ResetCommand.Execute(null);
            await Answer(git);
            await Settled(git);

            // a conflicting rebase is resolved by the AI while the client shows it working
            File.WriteAllText(aiDelay, "2");
            G(app, "checkout", "-b", "conflict-a");
            File.WriteAllText(Path.Combine(app, "README.md"), "ONE-A\nTWO\nthree\nfour\nfive\n");
            G(app, "commit", "-am", "Feature edit A");
            G(app, "checkout", "main");
            File.WriteAllText(Path.Combine(app, "README.md"), "ONE-MAIN\nTWO\nthree\nfour\nfive\n");
            G(app, "commit", "-am", "Main edit");
            git.History.RefreshCommand.Execute(null);
            await Task.Delay(100);
            await Until(() => git.History.Commits.Any(commit => commit.Subject == "Feature edit A"), "the feature branch in the history");
            GitRefViewModel conflict = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "conflict-a");
            conflict.RebaseSelectedCommand.Execute(null);
            await Until(() => git.IsPrompting, "the rebase question");
            git.ConfirmCommand.Execute(null);
            await Until(() => git.IsWorking, "the working state");
            Assert.True(await WaitForWorkingPanel(window));
            await Until(() => !git.IsWorking && !git.IsPrompting, "the rebase to finish", 40000);
            Assert.Equal("main", G(app, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal("1", G(app, "rev-list", "--count", "main..conflict-a"));
            Assert.Equal("0", G(app, "rev-list", "--count", "conflict-a..main"));
            Assert.DoesNotContain("<<<<", G(app, "show", "conflict-a:README.md"));
            Assert.Contains(workspace.Chat.Messages, message => message.Text == "resolved conflicts");

            // merge fast-forwards, pushes and deletes the branch everywhere
            git.History.RefreshCommand.Execute(null);
            await Task.Delay(100);
            await Until(() => git.History.Commits.SelectMany(commit => commit.Refs).Any(item => item.Name == "conflict-a"), "conflict-a again");
            conflict = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "conflict-a");
            conflict.MergeSelectedCommand.Execute(null);
            await Until(() => git.IsPrompting, "the merge question");
            git.ConfirmCommand.Execute(null);
            await Until(() => G(app, "branch", "--list", "conflict-a").Length == 0, "the merged branch deleted", 40000);
            await Settled(git);
            Assert.Equal(string.Empty, G(origin, "branch", "--list", "conflict-a"));
            Assert.Equal("Feature edit A", G(app, "log", "-1", "--format=%s", "main"));
            Assert.Equal(G(app, "rev-parse", "main"), G(origin, "rev-parse", "main"));

            // squash asks for a message
            G(app, "checkout", "-b", "multi");
            foreach (int number in new[] { 1, 2, 3 })
            {
                File.WriteAllText(Path.Combine(app, $"m{number}.txt"), "m");
                G(app, "add", "-A");
                G(app, "commit", "-m", $"step {number}");
            }

            G(app, "push", "origin", "multi");
            G(app, "checkout", "main");
            git.History.RefreshCommand.Execute(null);
            await Task.Delay(100);
            await Until(() => git.History.Commits.SelectMany(commit => commit.Refs).Any(item => item.Name == "multi"), "multi");
            GitRefViewModel multi = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "multi");
            multi.SquashSelectedCommand.Execute(null);
            await Until(() => git.IsPrompting, "the squash question");
            Assert.Equal("step 3", git.PromptText);
            Assert.Contains("3 commits", git.PromptHint);
            git.PromptText = "All steps";
            git.ConfirmCommand.Execute(null);
            await Settled(git);
            await Until(() => G(app, "rev-list", "--count", "main..multi") == "1", "the squash");
            Assert.Equal("All steps", G(origin, "log", "-1", "--format=%s", "multi"));

            // nothing to squash and dirty rebase are explained
            multi = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "multi");
            multi.SquashCurrentCommand.Execute(null);
            await Until(() => git.Prompt == GitPrompt.Notice, "the notice");
            Assert.Contains("has no commits", git.PromptHint);
            await Answer(git);
            File.WriteAllText(Path.Combine(app, "pending.txt"), "p");
            await Until(() => git.HasChanges, "pending");
            multi.RebaseSelectedCommand.Execute(null);
            await Until(() => git.Prompt == GitPrompt.Notice, "the dirty notice");
            Assert.Contains("clean working tree", git.PromptHint);
            await Answer(git);
            git.ResetCommand.Execute(null);
            await Answer(git);
            await Settled(git);

            // deleting a tag and a branch
            tag = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "v1.0");
            tag.DeleteCommand.Execute(null);
            await Task.Delay(100);
            await Answer(git);
            await Until(() => G(app, "tag", "--list", "v1.0").Length == 0, "tag deleted");
            Assert.Equal(string.Empty, G(origin, "tag", "--list", "v1.0"));
            multi = git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "multi");
            multi.DeleteCommand.Execute(null);
            await Task.Delay(100);
            await Answer(git);
            await Until(() => G(app, "branch", "--list", "multi").Length == 0, "branch deleted");
            Assert.Equal(string.Empty, G(origin, "branch", "--list", "multi"));

            // cancel undoes a long action
            File.WriteAllText(aiDelay, "8");
            G(app, "checkout", "-b", "conflict-b");
            File.WriteAllText(Path.Combine(app, "c.txt"), "B\n");
            G(app, "add", "-A");
            G(app, "commit", "-m", "B edit");
            G(app, "checkout", "main");
            File.WriteAllText(Path.Combine(app, "c.txt"), "MAIN\n");
            G(app, "add", "-A");
            G(app, "commit", "-m", "Main c");
            string before = G(app, "rev-parse", "conflict-b");
            git.History.RefreshCommand.Execute(null);
            await Task.Delay(100);
            await Until(() => git.History.Commits.SelectMany(commit => commit.Refs).Any(item => item.Name == "conflict-b"), "conflict-b");
            git.History.Commits.SelectMany(commit => commit.Refs).Single(item => item.Name == "conflict-b").RebaseSelectedCommand.Execute(null);
            await Until(() => git.IsPrompting, "the rebase question");
            git.ConfirmCommand.Execute(null);
            await Until(() => git.IsWorking, "working");
            await Task.Delay(1000);
            git.CancelWorkCommand.Execute(null);
            await Until(() => git.Error is not null && git.Error.Contains("cancelled"), "the cancel message", 30000);
            git.CancelCommand.Execute(null);
            await Settled(git);
            Assert.Equal(before, G(app, "rev-parse", "conflict-b"));
            Assert.Equal("main", G(app, "rev-parse", "--abbrev-ref", "HEAD"));
            Assert.Equal(string.Empty, G(app, "status", "--porcelain"));
            File.WriteAllText(aiDelay, "0");

            // the remote can be changed
            git.RemoteCommand.Execute(null);
            Assert.Equal(origin, git.PromptText);
            await Answer(git, temp.Combine("other.git"));
            await Settled(git);
            Assert.Equal(temp.Combine("other.git"), G(app, "remote", "get-url", "origin"));
            git.RemoteCommand.Execute(null);
            await Answer(git, origin);
            await Settled(git);

            // a plain workspace becomes a repository through the remote
            string plain = Path.Combine(clones, "plain");
            Directory.CreateDirectory(plain);
            home.NewWorkspaceCommand.Execute(null);
            await Until(() => main.Current is NewWorkspaceViewModel { Browser.CanChoose: true }, "the new workspace screen");
            NewWorkspaceViewModel creating = (NewWorkspaceViewModel)main.Current!;
            creating.Browser.NewFolderName = "plain2";
            creating.Browser.CreateFolderCommand.Execute(null);
            await Until(() => creating.Browser.CurrentPath.EndsWith("plain2"), "inside the new folder");
            creating.StartCommand.Execute(null);
            await Until(() => main.Current is HomeViewModel { Active.Workspace.Folder: not null } created && created.Active.Workspace.Folder.EndsWith("plain2"), "the plain workspace");
            WorkspaceViewModel plainWorkspace = ((HomeViewModel)main.Current!).Active!;
            await Task.Delay(300);
            Assert.False(plainWorkspace.Git.IsRepo);
            Assert.False(plainWorkspace.ShowHistoryTab);
            plainWorkspace.Git.RemoteCommand.Execute(null);
            Assert.Contains("makes the workspace a git repository", plainWorkspace.Git.PromptHint);
            await Answer(plainWorkspace.Git, origin);
            await Until(() => plainWorkspace.Git.IsRepo, "the repository");
            Assert.True(plainWorkspace.ShowHistoryTab);
            Assert.Equal(origin, G(Path.Combine(clones, "plain2"), "remote", "get-url", "origin"));

            window.Close();
            return true;
        });
    }

    private async Task<bool> WaitForWorkingPanel(Window window)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (Texts(window).Contains("Working…"))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private async Task ShowFiles(WorkspaceViewModel workspace, ClientLayout layout)
    {
        if (layout == ClientLayout.Tabs)
        {
            workspace.ShowFilesCommand.Execute(null);
        }

        await Until(() => workspace.Files.Nodes.Count > 0, "the files");
    }

    private async Task ShowHistory(WorkspaceViewModel workspace, ClientLayout layout)
    {
        workspace.ShowHistoryCommand.Execute(null);
        await Until(() => workspace.ShowHistoryView, "the history view");
    }
}
