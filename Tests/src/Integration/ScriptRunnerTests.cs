namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ScriptRunnerTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly ScriptRunner runner;

    private readonly WorkspaceInfo workspace;

    private readonly IReadOnlyDictionary<string, string> environment = new Dictionary<string, string>();

    public ScriptRunnerTests()
    {
        string waiting = temp.Combine("waiting-dotnet");
        File.WriteAllText(waiting, "#!/bin/sh\necho \"started $(basename \"$3\")\"\nIFS= read -r line\necho \"finished $line\"\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(waiting, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        runner = new ScriptRunner(new EventHub());
        runner.Configure(new ServerConfig { Port = 0, Key = "k", DotnetCommand = waiting }, stopping.Token);
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = temp.Combine("work") };
        Directory.CreateDirectory(workspace.Folder);

        foreach (string name in (string[])["a.cs", "b.cs", "c.cs"])
        {
            File.WriteAllText(Path.Combine(workspace.Folder, name), "// not run by the fake");
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        runner.StopAll(workspace.Id).GetAwaiter().GetResult();
        temp.Dispose();
    }

    private ScriptRun Find(string path) => runner.List(workspace.Id).Runs.Single(run => run.Path == path);

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private Task WriteFile(string name, string content) => File.WriteAllTextAsync(Path.Combine(workspace.Folder, name), content);

    private Task Run(string path) => runner.Run(workspace, path, environment, CancellationToken.None);

    [Fact]
    public async Task Run_StartsSeveralScriptsAtOnceAndEachFinishesOnItsOwn()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Run("a.cs");
        await Run("b.cs");
        await Until(() => Find("a.cs").Output.Contains("started a.cs") && Find("b.cs").Output.Contains("started b.cs"));

        Assert.Equal([ScriptState.Running, ScriptState.Running], [Find("a.cs").State, Find("b.cs").State]);
        Assert.True(runner.IsRunning(workspace.Id));

        await runner.SendInput(workspace.Id, "b.cs", "two", CancellationToken.None);
        await Until(() => Find("b.cs").State == ScriptState.Succeeded);

        Assert.Equal(ScriptState.Running, Find("a.cs").State);
        Assert.True(runner.IsRunning(workspace.Id));

        await runner.SendInput(workspace.Id, "a.cs", "one", CancellationToken.None);
        await Until(() => Find("a.cs").State == ScriptState.Succeeded);

        Assert.Contains("finished one", Find("a.cs").Output);
        Assert.Contains("finished two", Find("b.cs").Output);
        Assert.False(runner.IsRunning(workspace.Id));
    }

    [Fact]
    public async Task Run_RefusesAFileThatIsRunningAndReplacesItsFinishedRun()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Run("a.cs");

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => Run("a.cs"));

        Assert.Equal("'a.cs' is already running in this workspace.", refused.Message);

        await Until(() => Find("a.cs").Output.Contains("started a.cs"));
        await runner.SendInput(workspace.Id, "a.cs", "one", CancellationToken.None);
        await Until(() => Find("a.cs").State == ScriptState.Succeeded);
        await Run("a.cs");
        await Until(() => Find("a.cs").State == ScriptState.Running && Find("a.cs").Output.Contains("started a.cs"));

        Assert.DoesNotContain("finished", Find("a.cs").Output);
        Assert.Single(runner.List(workspace.Id).Runs);
    }

    [Fact]
    public async Task Run_RunsATaskBesideAScriptButRefusesOneThatListsAScriptAlreadyRunning()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await WriteFile("t.task", "b.cs\n-\nc.cs\n");
        await WriteFile("clash.task", "a.cs\n");
        await Run("a.cs");
        await Run("t.task");
        await Until(() => Find("b.cs").State == ScriptState.Running);

        RequestException listed = await Assert.ThrowsAsync<RequestException>(() => Run("clash.task"));
        RequestException pending = await Assert.ThrowsAsync<RequestException>(() => Run("c.cs"));

        Assert.Equal("'a.cs' is already running in this workspace.", listed.Message);
        Assert.Equal("'c.cs' is already running in this workspace.", pending.Message);
        Assert.Equal([ScriptState.Running, ScriptState.Running, ScriptState.Running, ScriptState.Pending], [Find("a.cs").State, Find("t.task").State, Find("b.cs").State, Find("c.cs").State]);
        Assert.Equal(["a.cs", "t.task", "b.cs", "c.cs"], runner.List(workspace.Id).Runs.Select(run => run.Path));
    }

    [Fact]
    public async Task Stop_EndsOnlyTheRunItNamesAndStopAllEndsTheRest()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await Run("a.cs");
        await Run("b.cs");
        await Until(() => Find("a.cs").Output.Contains("started a.cs") && Find("b.cs").Output.Contains("started b.cs"));

        runner.Stop(workspace.Id, "a.cs");
        await Until(() => Find("a.cs").State == ScriptState.Stopped);

        Assert.Equal(ScriptState.Running, Find("b.cs").State);
        Assert.True(runner.IsRunning(workspace.Id));

        await runner.StopAll(workspace.Id);

        Assert.Equal(ScriptState.Stopped, Find("b.cs").State);
        Assert.False(runner.IsRunning(workspace.Id));
    }

    [Fact]
    public async Task Run_KeepsTheRunsOfOneWorkspaceApartFromThoseOfAnother()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WorkspaceInfo other = new() { Id = Guid.NewGuid(), Folder = workspace.Folder };

        await Run("a.cs");
        await runner.Run(other, "a.cs", environment, CancellationToken.None);

        Assert.True(runner.IsRunning(workspace.Id));
        Assert.True(runner.IsRunning(other.Id));

        await runner.StopAll(other.Id);

        Assert.False(runner.IsRunning(other.Id));
        Assert.True(runner.IsRunning(workspace.Id));
    }
}
