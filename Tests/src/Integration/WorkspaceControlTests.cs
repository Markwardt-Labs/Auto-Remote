namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class WorkspaceControlTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly EventHub hub = new();

    private readonly WorkspaceActivity activity;

    private readonly PromptService prompts;

    private readonly WorkspaceControl control;

    private readonly WorkspaceInfo workspace;

    private readonly string work;

    public WorkspaceControlTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        work = temp.Combine("work");
        Directory.CreateDirectory(work);
        G("init", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "orig\n");
        G("add", "-A");
        G("commit", "-m", "base");
        File.WriteAllText(Path.Combine(work, "mine.txt"), "untracked\n");

        string fake = temp.Combine("fake-claude.sh");
        File.WriteAllText(fake, "#!/bin/sh\nwhile IFS= read -r line; do\n  echo changed > \"$PWD/a.txt\"; echo new > \"$PWD/created.txt\"\n  printf '{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"working\"}]}}\\n'\n  sleep 60\ndone\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = work };
        Mock<IWorkspaceManager> workspaces = new();
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        Mock<IWorkspaceStore> store = new();
        int index = 0;
        store.Setup(x => x.AppendHistory(workspace.Id, It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => index++);
        GitService git = new();
        activity = new WorkspaceActivity(hub);
        WorkspaceSnapshots snapshots = new(git);
        prompts = new PromptService(workspaces.Object, store.Object, new AiRunner(new ProcessFreezer()), hub, activity, snapshots);
        prompts.Configure(new ServerConfig { Port = 0, Key = "k", ClaudeCommand = fake, ClaudeArguments = [] }, CancellationToken.None);
        control = new WorkspaceControl(activity, prompts, Mock.Of<IScriptRunner>(), Mock.Of<IWorkspaceVersioning>(), snapshots, store.Object, workspaces.Object, hub);
    }

    public void Dispose() => temp.Dispose();

    private string G(params string[] arguments)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private string Read(string name) => File.ReadAllText(Path.Combine(work, name)).Trim();

    private async Task StartWork()
    {
        await prompts.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => File.Exists(Path.Combine(work, "created.txt")));
        Assert.Equal(WorkspaceState.Working, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Cancel_RevertsWhatTheAiChangedAndReturnsToReady()
    {
        await StartWork();

        await control.Cancel(workspace.Id);

        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.False(prompts.IsBusy(workspace.Id));
        Assert.Equal("orig", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(work, "created.txt")));
        Assert.Equal("untracked", Read("mine.txt"));
    }

    [Fact]
    public async Task Stop_KeepsWhatTheAiChangedAndReturnsToReady()
    {
        await StartWork();

        await control.Stop(workspace.Id);

        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.False(prompts.IsBusy(workspace.Id));
        Assert.Equal("changed", Read("a.txt"));
        Assert.Equal("new", Read("created.txt"));
    }

    [Fact]
    public async Task AfterStoppingTheAiTakesPromptsAgainAndCanBeCancelledAgain()
    {
        await StartWork();
        await control.Stop(workspace.Id);
        File.WriteAllText(Path.Combine(work, "a.txt"), "orig\n");
        File.Delete(Path.Combine(work, "created.txt"));

        await StartWork();
        await control.Cancel(workspace.Id);

        Assert.Equal("orig", Read("a.txt"));
        Assert.False(File.Exists(Path.Combine(work, "created.txt")));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }
}
