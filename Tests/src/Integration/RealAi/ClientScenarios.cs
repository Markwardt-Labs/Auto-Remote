namespace Markwardt.AutoRemote.Tests.Integration.RealAi;

[Collection("Headless")]
public sealed class ClientScenarios(HeadlessFixture fixture)
{
    [RealAiTheory]
    [InlineData(AiService.ClaudeCode)]
    [InlineData(AiService.Codex)]
    public async Task TheChatFollowsALiveConversationWithoutGapsOrRepeatsEvenWhenItIsReadAgainWhileTheAiWorks(AiService service)
    {
        await using RealServer server = await RealServer.Start();
        WorkspaceInfo workspace = await server.Workspace("follow", service);

        (List<(MessageRole, string)> shown, List<(MessageRole, string)> stored) = await fixture.RunAsync<(List<(MessageRole, string)>, List<(MessageRole, string)>)>(async () =>
        {
            IServerConnection connection = await new IServerConnection.Factory(new IMessageChannel.Factory(), Mock.Of<ILog>()).Connect(server.Settings, CancellationToken.None);
            WorkspaceViewModel model = new(connection, workspace, ClientLayout.Panels);
            await model.Start();

            for (int number = 1; number <= 4; number++)
            {
                await connection.Prompt(workspace.Id, $"Run the shell command `sleep 2`, then reply with exactly: ANSWER{number}", CancellationToken.None);

                for (int attempt = 0; attempt < 400; attempt++)
                {
                    await Task.Delay(300);
                    await model.Chat.Resync();

                    if (!model.Chat.IsAiWorking && model.Chat.Messages.Any(message => message.Role == MessageRole.Assistant && message.Text.Contains($"ANSWER{number}", StringComparison.Ordinal)))
                    {
                        break;
                    }
                }
            }

            await model.Chat.Resync();
            ChatHistory history = await connection.GetHistory(workspace.Id, null, 200, CancellationToken.None);
            return (model.Chat.Messages.Select(message => (message.Role, message.Text.Trim())).ToList(), history.Messages.Select(message => (message.Role, message.Text.Trim())).ToList());
        });

        Assert.Equal(stored, shown);
        Assert.Equal(4, shown.Count(message => message.Item1 == MessageRole.User));
        Assert.Empty(server.Failures(workspace));
    }
}
