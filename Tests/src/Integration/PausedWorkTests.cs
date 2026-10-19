namespace Markwardt.AutoRemote.Tests.Integration;

[Collection("Git")]
public sealed class PausedWorkTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Guid session = Guid.NewGuid();

    private readonly WorkspaceInfo workspace;

    private readonly string work;

    private readonly string log;

    private readonly string fake;

    private readonly List<Stack> stacks = [];

    public PausedWorkTests()
    {
        Environment.SetEnvironmentVariable("GIT_AUTHOR_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_AUTHOR_EMAIL", "tester@example.com");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_NAME", "Tester");
        Environment.SetEnvironmentVariable("GIT_COMMITTER_EMAIL", "tester@example.com");
        work = temp.Combine("work");
        log = temp.Combine("calls.log");
        Directory.CreateDirectory(work);
        G("init", "-b", "main");
        File.WriteAllText(Path.Combine(work, "a.txt"), "orig\n");
        G("add", "-A");
        G("commit", "-m", "base");
        File.WriteAllText(Path.Combine(work, "mine.txt"), "untracked\n");
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = work, AiSessionId = session };
        fake = temp.Combine("fake-claude.sh");
        File.WriteAllText(fake, $"#!/bin/sh\necho \"START $@\" >> \"{log}\"\nwhile IFS= read -r line; do\n  echo \"LINE $line\" >> \"{log}\"\n  echo changed > \"$PWD/a.txt\"\n  printf '{{\"type\":\"assistant\",\"message\":{{\"content\":[{{\"type\":\"text\",\"text\":\"working\"}}]}}}}\\n'\n  i=0\n  while true; do i=$((i+1)); echo $i > \"$PWD/tick.txt\"; sleep 0.05; done\ndone\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        foreach (Stack stack in stacks)
        {
            stack.Server.Cancel();
            stack.Prompts.Interrupt(workspace.Id).GetAwaiter().GetResult();
        }

        temp.Dispose();
    }

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
            await Task.Delay(30);
        }

        Assert.True(condition());
    }

    private int Ticks => File.Exists(Path.Combine(work, "tick.txt")) && int.TryParse(File.ReadAllText(Path.Combine(work, "tick.txt")).Trim(), out int value) ? value : 0;

    private string Calls => File.Exists(log) ? File.ReadAllText(log) : string.Empty;

    private async Task<Stack> Boot()
    {
        Mock<IWorkspaceManager> workspaces = new();
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        workspaces.Setup(x => x.List(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [workspace] });
        WorkspaceStore store = new();
        store.Open(temp.Combine("data/workspaces.json"));
        EventHub hub = new();
        WorkspaceActivity activity = new(hub);
        GitService git = new();
        WorkspaceSnapshots snapshots = new(git);
        PromptService prompts = new(workspaces.Object, store, new AiRunner(new ProcessFreezer()), hub, activity, snapshots);
        prompts.Configure(new ServerConfig { Port = 0, Key = "k", ClaudeCommand = fake, ClaudeArguments = [] }, CancellationToken.None);
        WorkspaceControl control = new(activity, prompts, Mock.Of<IScriptRunner>(), Mock.Of<IWorkspaceVersioning>(), snapshots, store, workspaces.Object, hub);
        Stack stack = new(activity, prompts, control, new CancellationTokenSource());
        await control.Restore(stack.Server.Token);
        stacks.Add(stack);
        return stack;
    }

    private async Task<Stack> PausedBeforeARestart()
    {
        Stack first = await Boot();
        await first.Prompts.Prompt(workspace.Id, "refactor everything", CancellationToken.None);
        await Until(() => Ticks > 2);
        await first.Control.Pause(workspace.Id);
        await Until(() => File.Exists(temp.Combine("data/paused.json")));
        int frozen = Ticks;
        await Task.Delay(300);
        Assert.Equal(frozen, Ticks);
        first.Server.Cancel();
        await first.Prompts.Interrupt(workspace.Id);
        await Task.Delay(200);
        Assert.True(File.Exists(temp.Combine("data/paused.json")));
        return await Boot();
    }

    [Fact]
    public async Task Pause_FreezesTheAiAndResumeLetsItCarryOnInTheSameProcess()
    {
        Stack stack = await Boot();
        await stack.Prompts.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => Ticks > 2);

        await stack.Control.Pause(workspace.Id);
        int frozen = Ticks;
        await Task.Delay(400);
        Assert.Equal(frozen, Ticks);
        Assert.Equal(WorkspaceState.Paused, stack.Activity.Get(workspace.Id));

        await stack.Control.Resume(workspace.Id);
        await Until(() => Ticks > frozen + 2);

        Assert.Equal(WorkspaceState.Working, stack.Activity.Get(workspace.Id));
        Assert.Equal(1, Calls.Split('\n').Count(line => line.StartsWith("START", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task APauseSurvivesTheServerRestartingAndCancelThenRevertsTheWork()
    {
        Stack restarted = await PausedBeforeARestart();

        Assert.Equal(WorkspaceState.Paused, restarted.Activity.Get(workspace.Id));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(work, "a.txt")).Trim());

        await restarted.Control.Cancel(workspace.Id);

        Assert.Equal(WorkspaceState.Ready, restarted.Activity.Get(workspace.Id));
        Assert.Equal("orig", File.ReadAllText(Path.Combine(work, "a.txt")).Trim());
        Assert.False(File.Exists(Path.Combine(work, "tick.txt")));
        Assert.Equal("untracked", File.ReadAllText(Path.Combine(work, "mine.txt")).Trim());
        await Until(() => !File.Exists(temp.Combine("data/paused.json")));
    }

    [Fact]
    public async Task APauseSurvivesTheServerRestartingAndResumeStartsTheAiAgainInItsSession()
    {
        Stack restarted = await PausedBeforeARestart();

        await restarted.Control.Resume(workspace.Id);
        await Until(() => Calls.Split('\n').Count(line => line.StartsWith("START", StringComparison.Ordinal)) == 2);

        Assert.Equal(WorkspaceState.Working, restarted.Activity.Get(workspace.Id));
        string[] calls = Calls.Split('\n');
        Assert.Contains($"--resume {session}", calls.Last(line => line.StartsWith("START", StringComparison.Ordinal)));
        await Until(() => Calls.Contains("server was restarted", StringComparison.Ordinal));
        await Until(() => !File.Exists(temp.Combine("data/paused.json")));
    }

    [Fact]
    public async Task APauseSurvivesTheServerRestartingAndStopLeavesTheWorkAsItIs()
    {
        Stack restarted = await PausedBeforeARestart();

        await restarted.Control.Stop(workspace.Id);

        Assert.Equal(WorkspaceState.Ready, restarted.Activity.Get(workspace.Id));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(work, "a.txt")).Trim());
        await Until(() => !File.Exists(temp.Combine("data/paused.json")));
    }

    private sealed record Stack(WorkspaceActivity Activity, PromptService Prompts, WorkspaceControl Control, CancellationTokenSource Server);
}
