namespace Markwardt.AutoRemote.Tests.Integration;

public sealed class ClaudeConversationTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly AiRunner runner = new();

    public void Dispose() => temp.Dispose();

    private IAiConversation Start(string outputLines, int exitCode = 0, string errors = "")
    {
        File.WriteAllText(temp.Combine("out.txt"), outputLines);
        File.WriteAllText(temp.Combine("err.txt"), errors);
        string fake = temp.Combine("fake.sh");
        File.WriteAllText(fake, $"#!/bin/sh\nIFS= read -r line\nprintf '%s\\n' \"$line\" > \"$PWD/received.txt\"\ncat \"$PWD/out.txt\"\ncat \"$PWD/err.txt\" >&2\nexit {exitCode}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return runner.Start(new ServerConfig { Port = 0, WebPort = 0, Key = "k", ClaudeCommand = fake }, AiService.ClaudeCode, temp.Path, AiModel.Sonnet, AiEffort.High, Guid.NewGuid(), false);
    }

    private async Task<List<AiOutput>> Collect(IAiConversation conversation)
    {
        await conversation.Send("do \"it\"", CancellationToken.None);
        List<AiOutput> outputs = [];

        await foreach (AiOutput output in conversation.Read(CancellationToken.None))
        {
            outputs.Add(output);
        }

        await conversation.DisposeAsync();
        return outputs;
    }

    [Fact]
    public async Task Read_ReportsTextOfTheMainAgentAndWhenTheWorkIsDone()
    {
        string lines = string.Join("\n",
            "{\"type\":\"system\",\"subtype\":\"init\"}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"first\"},{\"type\":\"text\",\"text\":\"second\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Bash\"}]}}",
            "{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_1\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"from a sub agent\"}]}}",
            "not json",
            "{\"type\":\"result\",\"is_error\":false,\"result\":\"second\"}") + "\n";

        List<AiOutput> outputs = await Collect(Start(lines));

        Assert.Equal([new AiOutput { Kind = AiOutputKind.Reply, Text = "first\n\nsecond" }, new AiOutput { Kind = AiOutputKind.Finished }], outputs);
        using JsonDocument sent = JsonDocument.Parse(await File.ReadAllTextAsync(temp.Combine("received.txt")));
        Assert.Equal("user", sent.RootElement.GetProperty("type").GetString());
        Assert.Equal("do \"it\"", sent.RootElement.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Read_ReportsAnErrorResultWithItsMessage()
    {
        List<AiOutput> outputs = await Collect(Start("{\"type\":\"result\",\"is_error\":true,\"errors\":[\"No conversation found with session ID: x\"]}\n", 1));

        Assert.Equal("No conversation found with session ID: x", Assert.Single(outputs, output => output.Kind == AiOutputKind.Failed).Text);
    }

    [Fact]
    public async Task Read_ReportsAProcessThatFailedWithItsErrorOutput()
    {
        List<AiOutput> outputs = await Collect(Start(string.Empty, 3, "bad flag\n"));

        Assert.Equal(new AiOutput { Kind = AiOutputKind.Failed, Text = "bad flag" }, Assert.Single(outputs));
    }

    [Fact]
    public async Task Send_AfterTheConversationEndedFails()
    {
        IAiConversation conversation = Start(string.Empty);
        await Collect(conversation);

        await Assert.ThrowsAsync<RequestException>(() => conversation.Send("x", CancellationToken.None));
    }

    [Fact]
    public void Start_FailsWhenTheExecutableIsMissing() => Assert.Throws<RequestException>(() => runner.Start(new ServerConfig { Port = 0, WebPort = 0, Key = "k", ClaudeCommand = "/definitely/missing" }, AiService.ClaudeCode, temp.Path, AiModel.Sonnet, AiEffort.High, Guid.NewGuid(), false));
}
