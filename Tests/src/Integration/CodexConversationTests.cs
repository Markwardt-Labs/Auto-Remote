namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class CodexConversationTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly AiRunner runner = new();

    public void Dispose() => temp.Dispose();

    private IAiConversation Start(string script, Guid? resume = null)
    {
        string fake = temp.Combine("fake-codex.sh");
        File.WriteAllText(fake, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$PWD/args.txt\"\ncat > \"$PWD/prompt-$$.txt\"\n" + script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return runner.Start(new ServerConfig { Port = 0, WebPort = 0, Key = "k", CodexCommand = fake, CodexArguments = ["--extra"] }, AiService.Codex, temp.Path, AiModel.Sol, AiEffort.XHigh, resume ?? Guid.NewGuid(), resume is not null);
    }

    private async Task<List<AiOutput>> Collect(IAiConversation conversation, params string[] prompts)
    {
        foreach (string prompt in prompts)
        {
            await conversation.Send(prompt, CancellationToken.None);
        }

        List<AiOutput> outputs = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        await foreach (AiOutput output in conversation.Read(timeout.Token))
        {
            outputs.Add(output);

            if (output.Kind is AiOutputKind.Finished or AiOutputKind.Failed)
            {
                break;
            }
        }

        await conversation.DisposeAsync();
        return outputs;
    }

    [Fact]
    public async Task Read_ReportsTheSessionTheRepliesAndWhenTheWorkIsDone()
    {
        string script = "printf '%s\\n' '{\"type\":\"thread.started\",\"thread_id\":\"01a12c78-393f-7351-aaea-c1b57f0c44fe\"}' '{\"type\":\"turn.started\"}' '{\"type\":\"item.completed\",\"item\":{\"id\":\"i0\",\"type\":\"error\",\"message\":\"warning\"}}' '{\"type\":\"item.completed\",\"item\":{\"id\":\"i1\",\"type\":\"agent_message\",\"text\":\"hi there\"}}' 'not json' '{\"type\":\"turn.completed\"}'\n";

        List<AiOutput> outputs = await Collect(Start(script), "do \"it\"");

        Assert.Equal([new AiOutput { Kind = AiOutputKind.Session, Text = "01a12c78-393f-7351-aaea-c1b57f0c44fe" }, new AiOutput { Kind = AiOutputKind.Reply, Text = "hi there" }, new AiOutput { Kind = AiOutputKind.Finished }], outputs);
        Assert.Equal("do \"it\"", File.ReadAllText(Directory.GetFiles(temp.Path, "prompt-*.txt").Single()));
        string arguments = File.ReadAllText(temp.Combine("args.txt"));
        Assert.StartsWith("exec --json --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox -m gpt-5.6-sol -c model_reasoning_effort=\"xhigh\" --extra -", arguments);
    }

    [Fact]
    public async Task Send_WhileWorkingRunsTheNextPromptInTheSameSessionAndFinishesOnce()
    {
        string script = "case \"$*\" in *resume*) n=second;; *) n=first;; esac\nsleep 0.3\nprintf '%s\\n' '{\"type\":\"thread.started\",\"thread_id\":\"01a12c78-393f-7351-aaea-c1b57f0c44fe\"}' \"{\\\"type\\\":\\\"item.completed\\\",\\\"item\\\":{\\\"type\\\":\\\"agent_message\\\",\\\"text\\\":\\\"$n\\\"}}\" '{\"type\":\"turn.completed\"}'\n";

        List<AiOutput> outputs = await Collect(Start(script), "one", "two");

        Assert.Equal(["first", "second"], outputs.Where(output => output.Kind == AiOutputKind.Reply).Select(output => output.Text));
        Assert.Equal(1, outputs.Count(output => output.Kind == AiOutputKind.Finished));
        Assert.Equal(AiOutputKind.Finished, outputs[^1].Kind);
        Assert.Contains("exec resume --json", File.ReadAllText(temp.Combine("args.txt")));
        Assert.Contains(" 01a12c78-393f-7351-aaea-c1b57f0c44fe -", File.ReadAllText(temp.Combine("args.txt")));
    }

    [Fact]
    public async Task Start_ResumesTheGivenSession()
    {
        Guid session = Guid.NewGuid();

        await Collect(Start("printf '%s\\n' '{\"type\":\"turn.completed\"}'\n", session), "again");

        Assert.Contains($"exec resume --json", File.ReadAllText(temp.Combine("args.txt")));
        Assert.Contains($" {session} -", File.ReadAllText(temp.Combine("args.txt")));
    }

    [Fact]
    public async Task Read_ReportsAFailedTurnWithItsMessage()
    {
        string script = "printf '%s\\n' '{\"type\":\"turn.failed\",\"error\":{\"message\":\"{\\\"type\\\":\\\"error\\\",\\\"error\\\":{\\\"message\\\":\\\"model not supported\\\"}}\"}}'\nexit 1\n";

        List<AiOutput> outputs = await Collect(Start(script), "go");

        Assert.Equal([new AiOutput { Kind = AiOutputKind.Failed, Text = "model not supported" }], outputs);
    }

    [Fact]
    public async Task Read_ReportsWhatCodexWroteToErrorsWhenItExitsWithoutAnswering()
    {
        List<AiOutput> outputs = await Collect(Start("echo 'no rollout found for thread id x' >&2\nexit 1\n"), "go");

        Assert.Equal([new AiOutput { Kind = AiOutputKind.Failed, Text = "no rollout found for thread id x" }], outputs);
    }

    [Fact]
    public async Task Read_ReportsAMissingExecutableAsAFailure()
    {
        IAiConversation conversation = runner.Start(new ServerConfig { Port = 0, WebPort = 0, Key = "k", CodexCommand = "/definitely/missing" }, AiService.Codex, temp.Path, AiModel.Terra, AiEffort.High, Guid.NewGuid(), false);

        List<AiOutput> outputs = await Collect(conversation, "go");

        Assert.Equal(AiOutputKind.Failed, Assert.Single(outputs).Kind);
        Assert.Contains("could not be started", outputs[0].Text);
    }
}
