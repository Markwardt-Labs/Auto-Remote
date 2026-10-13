namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class PromptServiceTests
{
    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IAiRunner> runner = new();

    private readonly Mock<IEventHub> hub = new();

    private readonly List<RemoteEvent> events = [];

    private readonly WorkspaceActivity activity = new();

    private readonly WorkspaceInfo workspace = new() { Id = Guid.NewGuid(), Folder = "/work/app" };

    private readonly PromptService service;

    private int nextIndex;

    public PromptServiceTests()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace);
        store.Setup(x => x.AppendHistory(workspace.Id, It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => nextIndex++);
        hub.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            lock (events)
            {
                events.Add(remoteEvent);
            }
        });
        service = new PromptService(workspaces.Object, store.Object, runner.Object, hub.Object, activity);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, CancellationToken.None);
    }

    private Conversation Start(Guid? session = null, bool? resume = null, AiModel model = AiModel.Sonnet, AiEffort effort = AiEffort.High)
    {
        Conversation conversation = new();
        runner.Setup(x => x.Start(It.IsAny<ServerConfig>(), It.IsAny<AiService>(), "/work/app", model, effort, It.Is<Guid>(id => session == null || id == session), It.Is<bool>(resumed => resume == null || resumed == resume))).Returns(conversation.Mock.Object);
        return conversation;
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private List<RemoteEvent> Events(RemoteEventKind kind)
    {
        lock (events)
        {
            return [.. events.Where(remoteEvent => remoteEvent.Kind == kind)];
        }
    }

    [Fact]
    public async Task Attach_StoresTheDecodedFileAndAnswersItsPath()
    {
        byte[] saved = [];
        store.Setup(x => x.SaveAttachment(workspace.Id, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).Callback<Guid, string, ReadOnlyMemory<byte>, CancellationToken>((_, _, data, _) => saved = data.ToArray()).ReturnsAsync("/srv/a.txt");

        string path = await service.Attach(workspace.Id, "a.txt", Convert.ToBase64String(new byte[] { 1, 2, 3 }), CancellationToken.None);

        Assert.Equal("/srv/a.txt", path);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved);
    }

    [Fact]
    public async Task Attach_RefusesInvalidAndOversizedData()
    {
        await Assert.ThrowsAsync<RequestException>(() => service.Attach(workspace.Id, "a.txt", "not base64!", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => service.Attach(workspace.Id, "a.txt", Convert.ToBase64String(new byte[2 * 1024 * 1024 + 1]), CancellationToken.None));
    }

    [Fact]
    public async Task Prompt_TakesTheWorkspaceIntoWorkingAndGivesItBackWhenTheAiFinishes()
    {
        Conversation conversation = Start();

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        Assert.Equal(WorkspaceState.Working, activity.Get(workspace.Id));
        await service.Prompt(workspace.Id, "and more", CancellationToken.None);

        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Finished });
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Theory]
    [InlineData(WorkspaceState.Running)]
    public async Task Prompt_IsRefusedWhileOtherWorkHoldsTheWorkspace(WorkspaceState busy)
    {
        Conversation conversation = Start();
        activity.TryEnter(workspace.Id, busy);

        await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Empty(conversation.Sent);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(busy, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_DuringAGitActionWorksWithoutChangingTheState()
    {
        Conversation conversation = Start();
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);

        await service.Prompt(workspace.Id, "resolve the conflicts", CancellationToken.None);
        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));

        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Finished });
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_StartsAiSessionRecordsMessagesAndAttachesItOnTheFirstReply()
    {
        Conversation conversation = Start(resume: false);

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);

        Assert.Equal(["hello"], conversation.Sent);
        Assert.True(service.IsBusy(workspace.Id));
        Assert.Equal(new ChatMessage { Role = MessageRole.User, Text = "hello" }, Assert.Single(Events(RemoteEventKind.MessageAdded)).Message);
        Assert.Equal(0, Events(RemoteEventKind.MessageAdded)[0].Index);
        workspaces.Verify(x => x.AttachAiSession(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Reply, Text = "hi there" });
        await Until(() => Events(RemoteEventKind.MessageAdded).Count == 2);

        Assert.Equal(new RemoteEvent { Kind = RemoteEventKind.MessageAdded, WorkspaceId = workspace.Id, Index = 1, Message = new ChatMessage { Role = MessageRole.Assistant, Text = "hi there" } }, Events(RemoteEventKind.MessageAdded)[1]);
        store.Verify(x => x.AppendHistory(workspace.Id, new ChatMessage { Role = MessageRole.Assistant, Text = "hi there" }, It.IsAny<CancellationToken>()), Times.Once);
        workspaces.Verify(x => x.AttachAiSession(workspace.Id, It.Is<Guid>(attached => attached != Guid.Empty), It.IsAny<CancellationToken>()), Times.Once);

        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Finished });
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal([true, false], Events(RemoteEventKind.WorkingChanged).Select(remoteEvent => remoteEvent.Working));
    }

    [Fact]
    public async Task Prompt_RunsOnTheWorkspacesServiceAndAttachesTheSessionItReports()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Service = AiService.Codex, Model = AiModel.Sol });
        Conversation conversation = new();
        runner.Setup(x => x.Start(It.IsAny<ServerConfig>(), AiService.Codex, "/work/app", AiModel.Sol, AiEffort.High, It.IsAny<Guid>(), false)).Returns(conversation.Mock.Object);
        Guid reported = Guid.NewGuid();

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Session, Text = reported.ToString() });
        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Reply, Text = "hi" });
        await Until(() => Events(RemoteEventKind.MessageAdded).Count == 2);

        workspaces.Verify(x => x.AttachAiSession(workspace.Id, reported, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_ResumesAttachedAiSession()
    {
        Guid attached = Guid.NewGuid();
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { AiSessionId = attached });
        Conversation conversation = Start(attached, true);

        await service.Prompt(workspace.Id, "more", CancellationToken.None);
        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Reply, Text = "ok" });
        await Until(() => Events(RemoteEventKind.MessageAdded).Count == 2);

        Assert.Equal(["more"], conversation.Sent);
        workspaces.Verify(x => x.AttachAiSession(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prompt_WhileTheAiWorksJoinsTheRunningConversation()
    {
        Conversation conversation = Start();

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);

        Assert.Equal(["one", "two"], conversation.Sent);
        runner.Verify(x => x.Start(It.IsAny<ServerConfig>(), It.IsAny<AiService>(), It.IsAny<string>(), It.IsAny<AiModel>(), It.IsAny<AiEffort>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Once);
        Assert.Equal(["one", "two"], Events(RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message?.Text));
        Assert.Single(Events(RemoteEventKind.WorkingChanged));
    }

    [Fact]
    public async Task Prompt_AfterTheAiFinishedReusesTheIdleConversation()
    {
        Conversation conversation = Start();
        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Finished });
        await Until(() => !service.IsBusy(workspace.Id));

        await service.Prompt(workspace.Id, "two", CancellationToken.None);

        Assert.Equal(["one", "two"], conversation.Sent);
        Assert.True(service.IsBusy(workspace.Id));
        runner.Verify(x => x.Start(It.IsAny<ServerConfig>(), It.IsAny<AiService>(), It.IsAny<string>(), It.IsAny<AiModel>(), It.IsAny<AiEffort>(), It.IsAny<Guid>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_StartsNewAiSessionAndRepeatsTheTextWhenTheAttachedOneIsGone()
    {
        Guid gone = Guid.NewGuid();
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { AiSessionId = gone });
        Conversation lost = Start(gone, true);
        Conversation fresh = new();
        runner.Setup(x => x.Start(It.IsAny<ServerConfig>(), It.IsAny<AiService>(), "/work/app", It.IsAny<AiModel>(), It.IsAny<AiEffort>(), It.Is<Guid>(id => id != gone), false)).Returns(fresh.Mock.Object);

        await service.Prompt(workspace.Id, "more", CancellationToken.None);
        lost.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Failed, Text = "No conversation found with session ID" });
        await Until(() => fresh.Sent.Count == 1);

        Assert.Equal(["more"], fresh.Sent);
        Assert.Empty(Events(RemoteEventKind.Failed));
        Assert.True(service.IsBusy(workspace.Id));

        fresh.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Reply, Text = "fresh" });
        await Until(() => Events(RemoteEventKind.MessageAdded).Count == 2);
        workspaces.Verify(x => x.AttachAiSession(workspace.Id, It.Is<Guid>(attached => attached != gone && attached != Guid.Empty), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_UsesTheWorkspacesModelAndEffort()
    {
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workspace with { Model = AiModel.Opus, Effort = AiEffort.Max });
        Conversation conversation = Start(model: AiModel.Opus, effort: AiEffort.Max);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);

        Assert.Equal(["go"], conversation.Sent);
    }

    [Fact]
    public async Task Prompt_RejectsEmptyText() => await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "  ", CancellationToken.None));

    [Fact]
    public async Task Failure_IsReportedStopsTheWorkAndTheNextPromptStartsAgain()
    {
        Conversation broken = Start();
        await service.Prompt(workspace.Id, "one", CancellationToken.None);

        broken.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Failed, Text = "boom" });
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal("boom", Assert.Single(Events(RemoteEventKind.Failed)).Error);
        broken.Mock.Verify(x => x.DisposeAsync(), Times.Once);

        Conversation fine = Start();
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        Assert.Equal(["two"], fine.Sent);
    }

    [Fact]
    public async Task ProcessEndingWhileWorking_IsReportedAsAFailure()
    {
        Conversation conversation = Start();
        await service.Prompt(workspace.Id, "one", CancellationToken.None);

        conversation.Output.Writer.TryComplete();
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal("The AI stopped.", Assert.Single(Events(RemoteEventKind.Failed)).Error);
    }

    [Fact]
    public async Task Prompt_WhenTheTextCannotBeSentFailsAndEndsTheConversation()
    {
        Conversation conversation = Start();
        conversation.Mock.Setup(x => x.Send(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Claude Code is no longer running."));

        await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "one", CancellationToken.None));

        Assert.False(service.IsBusy(workspace.Id));
        conversation.Mock.Verify(x => x.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Release_EndsOnlyAnIdleConversation()
    {
        Conversation conversation = Start();
        await service.Prompt(workspace.Id, "one", CancellationToken.None);

        await service.Release(workspace.Id);
        conversation.Mock.Verify(x => x.DisposeAsync(), Times.Never);

        conversation.Output.Writer.TryWrite(new AiOutput { Kind = AiOutputKind.Finished });
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Release(workspace.Id);

        conversation.Mock.Verify(x => x.DisposeAsync(), Times.Once);
        await service.Release(Guid.NewGuid());
    }

    [Fact]
    public async Task History_ReturnsTheNewestChunkThenEarlierOnes()
    {
        store.Setup(x => x.ReadHistory(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([.. Enumerable.Range(0, 10).Select(index => new ChatMessage { Role = MessageRole.User, Text = index.ToString() })]);

        ChatHistory newest = await service.History(workspace.Id, null, 3, CancellationToken.None);
        ChatHistory earlier = await service.History(workspace.Id, newest.Start, 5, CancellationToken.None);
        ChatHistory first = await service.History(workspace.Id, earlier.Start, 5, CancellationToken.None);

        Assert.Equal(7, newest.Start);
        Assert.Equal(["7", "8", "9"], newest.Messages.Select(message => message.Text));
        Assert.Equal(2, earlier.Start);
        Assert.Equal(["2", "3", "4", "5", "6"], earlier.Messages.Select(message => message.Text));
        Assert.Equal(0, first.Start);
        Assert.Equal(["0", "1"], first.Messages.Select(message => message.Text));
        Assert.Empty((await service.History(workspace.Id, 0, 5, CancellationToken.None)).Messages);
    }

    [Fact]
    public async Task History_DefaultsToAChunkAndReportsWhetherTheAiWorks()
    {
        store.Setup(x => x.ReadHistory(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync([.. Enumerable.Range(0, 300).Select(index => new ChatMessage { Role = MessageRole.User, Text = index.ToString() })]);
        Start();
        await service.Prompt(workspace.Id, "go", CancellationToken.None);

        ChatHistory defaulted = await service.History(workspace.Id, null, null, CancellationToken.None);
        ChatHistory capped = await service.History(workspace.Id, null, 5000, CancellationToken.None);

        Assert.Equal(50, defaulted.Messages.Count);
        Assert.Equal(200, capped.Messages.Count);
        Assert.True(defaulted.Working);
    }

    private sealed class Conversation
    {
        public Conversation()
        {
            Mock.Setup(x => x.Send(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns<string, CancellationToken>((text, _) =>
            {
                lock (Sent)
                {
                    Sent.Add(text);
                }

                return Task.CompletedTask;
            });
            Mock.Setup(x => x.Read(It.IsAny<CancellationToken>())).Returns<CancellationToken>(cancellation => Output.Reader.ReadAllAsync(cancellation));
            Mock.Setup(x => x.DisposeAsync()).Returns(() =>
            {
                Output.Writer.TryComplete();
                return ValueTask.CompletedTask;
            });
        }

        public Mock<IAiConversation> Mock { get; } = new();

        public Channel<AiOutput> Output { get; } = Channel.CreateUnbounded<AiOutput>();

        public List<string> Sent { get; } = [];
    }
}
