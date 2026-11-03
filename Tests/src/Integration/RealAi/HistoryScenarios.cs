namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

public sealed class HistoryScenarios
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task History_PagesThroughALongConversationWithoutGapsAndMatchesWhatTheLiveEventsShowed(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("history", service);
        int mark = server.Mark();

        for (int number = 1; number <= 5; number++)
        {
            await server.Ask(workspace, $"Reply with exactly: ANSWER{number}");
        }

        List<(MessageRole Role, string Text)> live = [.. server.Events().Skip(mark).Where(item => item.WorkspaceId == workspace.Id && item.Kind == RemoteEventKind.MessageAdded).Select(item => (item.Message!.Role, item.Message.Text.Trim()))];
        List<(MessageRole Role, string Text)> stored = [];
        string? cursor = null;
        int chunks = 0;

        do
        {
            ChatHistory chunk = await server.Connection.GetHistory(workspace.Id, cursor, 3, CancellationToken.None);
            Assert.True(chunk.Messages.Count <= 3);
            stored.InsertRange(0, chunk.Messages.Select(message => (message.Role, message.Text.Trim())));
            cursor = chunk.Older;
            chunks++;
        }
        while (cursor is not null && chunks < 50);

        Assert.Equal(live, stored);
        Assert.True(chunks >= 3, "chunks: " + chunks);
        Assert.Equal(5, stored.Count(message => message.Role == MessageRole.User));
        for (int number = 1; number <= 5; number++)
        {
            Assert.Contains(stored, message => message.Role == MessageRole.Assistant && message.Text.Contains($"ANSWER{number}", StringComparison.Ordinal));
        }
    }

    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task LongPromptsWithUnicodeQuotesAndNewlinesComeBackUnchangedInTheHistory(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("unicode", service);
        string filler = string.Join('\n', Enumerable.Range(1, 400).Select(line => $"Line {line}: \"quoted\" 'single' \\ backslash `tick` {{brace}} <tag> 日本語 émoji 🦓"));
        string prompt = $"Here is some background text, ignore it:\n{filler}\n\nNow reply with exactly the word on the last line of this message, nothing else.\nZEBRA🦓";

        string reply = await server.Ask(workspace, prompt);

        Assert.Contains("ZEBRA", reply, StringComparison.Ordinal);
        ChatHistory history = await server.Connection.GetHistory(workspace.Id, null, null, CancellationToken.None);
        Assert.Equal(prompt.Trim(), history.Messages.First(message => message.Role == MessageRole.User).Text.Trim());
        Assert.Empty(server.Failures(workspace));
    }
}
