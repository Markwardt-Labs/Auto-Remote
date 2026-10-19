namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class AiRunnerTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly AiRunner runner = new(new ProcessFreezer());

    public void Dispose() => temp.Dispose();

    private string Fake(string name)
    {
        string path = temp.Combine(name);
        File.WriteAllText(path, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$PWD/args.txt\"\nprintf '%s' \"$CLAUDE_CODE_DISABLE_BACKGROUND_TASKS\" > \"$PWD/env.txt\"\ncat > /dev/null\n");

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private async Task<string[]> Arguments()
    {
        string file = temp.Combine("args.txt");

        for (int attempt = 0; attempt < 100 && !File.Exists(file); attempt++)
        {
            await Task.Delay(50);
        }

        return await File.ReadAllLinesAsync(file);
    }

    [Fact]
    public async Task Claude_RunsInTheForegroundWithoutSubagentsOrBackgroundWork()
    {
        ServerConfig config = new() { Port = 0, Key = "k", ClaudeCommand = Fake("claude"), ClaudeArguments = ["--extra"] };

        await using IAiConversation conversation = runner.Start(config, AiService.ClaudeCode, temp.Path, AiModel.Sonnet, AiEffort.High, Guid.NewGuid(), false);
        string[] arguments = await Arguments();

        int disallowed = Array.IndexOf(arguments, "--disallowedTools");
        Assert.True(disallowed >= 0);
        Assert.All(new[] { "Agent", "Task", "Workflow", "Monitor", "ScheduleWakeup", "CronCreate" }, tool => Assert.Contains(tool, arguments[disallowed + 1].Split(',')));
        Assert.Contains("--append-system-prompt", arguments);
        string prompt = arguments[Array.IndexOf(arguments, "--append-system-prompt") + 1];
        Assert.Contains("foreground", prompt);
        Assert.Contains("status update", prompt);
        Assert.Contains("every five minutes", prompt);
        Assert.True(Array.IndexOf(arguments, "--extra") > disallowed);
        Assert.Equal("1", await File.ReadAllTextAsync(temp.Combine("env.txt")));
    }

    [Fact]
    public async Task Codex_RunsWithoutSubagentsOrBackgroundSessions()
    {
        ServerConfig config = new() { Port = 0, Key = "k", CodexCommand = Fake("codex"), CodexArguments = [] };

        await using IAiConversation conversation = runner.Start(config, AiService.Codex, temp.Path, AiModel.Terra, AiEffort.Medium, Guid.NewGuid(), false);
        await conversation.Send("go", CancellationToken.None);
        string[] arguments = await Arguments();

        string joined = string.Join(' ', arguments);
        Assert.Contains("--disable multi_agent", joined);
        Assert.Contains("--disable unified_exec", joined);
        Assert.Contains("developer_instructions=\"", joined);
        Assert.Contains("status update", joined);
        Assert.Contains("every five minutes", joined);
    }
}
