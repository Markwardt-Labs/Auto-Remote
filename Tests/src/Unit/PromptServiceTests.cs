namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class PromptServiceTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly FakePrompter prompter = new();

    private readonly HostMock host;

    private readonly Mock<IEventHub> hub = new();

    private readonly List<RemoteEvent> events = [];

    private readonly WorkspaceActivity activity;

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly CancellationTokenSource stopping = new();

    private readonly Dictionary<Guid, List<ChatMessage>> chats = [];

    private WorkspaceInfo workspace;

    private PromptService service;

    public PromptServiceTests()
    {
        workspace = new WorkspaceInfo { Id = Guid.NewGuid(), Folder = temp.Path };
        hub.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(remoteEvent =>
        {
            lock (events)
            {
                events.Add(remoteEvent);
            }
        });
        store.Setup(x => x.LoadMessages(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) =>
        {
            lock (chats)
            {
                return chats.TryGetValue(id, out List<ChatMessage>? list) ? [.. list] : null;
            }
        });
        store.Setup(x => x.AppendMessage(It.IsAny<Guid>(), It.IsAny<ChatMessage>(), It.IsAny<CancellationToken>())).Returns((Guid id, ChatMessage message, CancellationToken _) =>
        {
            lock (chats)
            {
                chats.TryAdd(id, []);
                chats[id].Add(message);
            }

            return Task.CompletedTask;
        });
        store.Setup(x => x.SaveMessages(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>())).Returns((Guid id, IReadOnlyList<ChatMessage> messages, CancellationToken _) =>
        {
            lock (chats)
            {
                chats[id] = [.. messages];
            }

            return Task.CompletedTask;
        });
        activity = new WorkspaceActivity(hub.Object);
        host = new HostMock(prompter);
        service = new PromptService(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object);
        Use(workspace);
        service.Configure(stopping.Token);
    }

    public void Dispose() => temp.Dispose();

    private FakePromptSession Session => prompter.Sessions[^1];

    private void Use(WorkspaceInfo info)
    {
        workspace = info;
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => workspace);
        workspaces.Setup(x => x.SetOverrides(workspace.Id, It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, string? review, string? expand, int? minutes, CancellationToken _) => workspace = workspace with { ReviewPrompt = string.IsNullOrWhiteSpace(review) ? null : review.Trim(), ExpandPrompt = string.IsNullOrWhiteSpace(expand) ? null : expand.Trim(), StatusMinutes = minutes });
        workspaces.Setup(x => x.SetSandbox(workspace.Id, It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid _, bool sandboxed, CancellationToken _) => workspace = workspace with { IsSandboxed = sandboxed });
        workspaces.Setup(x => x.AttachSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, session, _) => workspace = workspace with { SessionId = session }).Returns(Task.CompletedTask);
    }

    private PromptService Restart()
    {
        service = new PromptService(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object);
        service.Configure(stopping.Token);
        return service;
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private async Task Settled() => await Until(() => !service.IsBusy(workspace.Id) && Events(RemoteEventKind.WorkingChanged).Count(remoteEvent => remoteEvent.Working == true) == Events(RemoteEventKind.WorkingChanged).Count(remoteEvent => remoteEvent.Working == false));

    private List<RemoteEvent> Events(RemoteEventKind kind)
    {
        lock (events)
        {
            return [.. events.Where(remoteEvent => remoteEvent.Kind == kind)];
        }
    }

    private List<string> Messages(MessageRole role) => [.. Events(RemoteEventKind.MessageAdded).Where(remoteEvent => remoteEvent.Message!.Role == role && !remoteEvent.Message.IsWork).Select(remoteEvent => remoteEvent.Message!.Text)];

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
    public async Task Prompt_LetsTheAiReadTheWorkspacesAttachmentsAlsoAfterItsSessionWasOpenedAgain()
    {
        string attached = temp.Combine("attachments");
        store.Setup(x => x.AttachmentsFolder(workspace.Id)).Returns(attached);
        prompter.OnCreate = session => session.Reply("ok");
        await service.Prompt(workspace.Id, "nothing attached yet", CancellationToken.None);
        await Settled();
        Assert.Empty(Session.SharedFolders);

        Directory.CreateDirectory(attached);
        await service.Prompt(workspace.Id, "read it", CancellationToken.None);
        await Settled();
        Assert.Equal([attached], Session.SharedFolders);

        await service.Close();
        await service.Prompt(workspace.Id, "read it again", CancellationToken.None);
        await Settled();
        Assert.Equal([attached], Session.SharedFolders);
    }

    [Fact]
    public async Task Attach_LetsAnOpenSessionReadTheFileAtOnce()
    {
        string attached = temp.Combine("attachments");
        store.Setup(x => x.AttachmentsFolder(workspace.Id)).Returns(attached);
        store.Setup(x => x.SaveAttachment(workspace.Id, "a.txt", It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Directory.CreateDirectory(attached).FullName + "/a.txt");
        prompter.OnCreate = session => session.Reply("ok");
        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Settled();

        await service.Attach(workspace.Id, "a.txt", Convert.ToBase64String(new byte[] { 1 }), CancellationToken.None);

        Assert.Equal([attached], Session.SharedFolders);
    }

    [Fact]
    public async Task Prompt_RecordsTheToolCallsAndResultsAsWorkMessagesBesideWhatTheAiSaidAndPublishesThem()
    {
        string longOutput = new('x', 2500);
        prompter.OnCreate = session => session.ReplyWith(_ =>
        [
            new PromptToolCallEvent { Id = "1", Name = "shell", Input = "{\"command\":\"ls -la\"}" },
            new PromptToolResultEvent { Id = "1", Output = "a.txt" },
            new PromptToolCallEvent { Id = "2", Server = "liveai", Name = "fetch_url", Input = "{\"url\":\"https://x.test\",\"max\":5}" },
            new PromptToolResultEvent { Id = "2", Output = "no such host", IsError = true },
            new PromptToolCallEvent { Id = "3", Name = "idle", Input = "not json" },
            new PromptToolResultEvent { Id = "3", Output = "  " },
            new PromptToolResultEvent { Id = "4", Output = longOutput },
            new PromptMessageEvent { Text = "done" }
        ]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        List<ChatMessage> published = [.. Events(RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message!)];
        ChatMessage[] work = [.. published.Where(message => message.IsWork)];

        Assert.Equal(["go", "done"], published.Where(message => !message.IsWork).Select(message => message.Text));
        Assert.Equal(["Run ls -la", "→ a.txt", "Fetch https://x.test", "→ Failed: no such host", "idle", "→ No output", "→ 1 line"], work.Select(message => message.Text));
        Assert.All(work, message => Assert.Equal(MessageRole.Assistant, message.Role));
        Assert.All(work, message => Assert.InRange(message.Time!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant, MessageRole.Assistant], chats[workspace.Id].Select(message => message.Role));
        Assert.Equal([false, true, true, true, true, true, true, true, false], chats[workspace.Id].Select(message => message.IsWork));
    }

    [Theory]
    [InlineData(false, new[] { "first", "answer one", "second", "answer two" })]
    [InlineData(true, new[] { "first", "ls", "answer one", "second", "cat", "answer two" })]
    public async Task History_LeavesTheWorkOutUnlessTheWorkspaceShowsItAndCountsOnlyWhatItReturns(bool show, string[] expected)
    {
        Use(workspace with { ShowWork = show });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        chats[workspace.Id] =
        [
            new ChatMessage { Role = MessageRole.User, Text = "first", Time = now },
            new ChatMessage { Role = MessageRole.Assistant, Text = "ls", Time = now, IsWork = true },
            new ChatMessage { Role = MessageRole.Assistant, Text = "answer one", Time = now },
            new ChatMessage { Role = MessageRole.User, Text = "second", Time = now },
            new ChatMessage { Role = MessageRole.Assistant, Text = "cat", Time = now, IsWork = true },
            new ChatMessage { Role = MessageRole.Assistant, Text = "answer two", Time = now }
        ];

        ChatHistory all = await service.History(workspace.Id, null, null, null, CancellationToken.None);
        ChatHistory newest = await service.History(workspace.Id, null, 2, null, CancellationToken.None);
        ChatHistory older = await service.History(workspace.Id, newest.Older, 2, null, CancellationToken.None);

        Assert.Equal(expected, all.Messages.Select(message => message.Text));
        Assert.Equal(show, all.ShowWork);
        Assert.Equal(expected.TakeLast(2), newest.Messages.Select(message => message.Text));
        Assert.Equal((expected.Length - 2).ToString(), newest.Older);
        Assert.Equal(expected.Skip(expected.Length - 4).Take(2), older.Messages.Select(message => message.Text));
        Assert.Equal(show, older.ShowWork);
    }

    private IEnumerable<PromptEvent> Writing(ManualResetEventSlim? gate = null)
    {
        yield return new PromptDeltaEvent { Text = "Hel" };
        Thread.Sleep(80);
        gate?.Wait(TimeSpan.FromSeconds(10));
        yield return new PromptDeltaEvent { Text = "lo " };
        Thread.Sleep(80);
        yield return new PromptDeltaEvent { Text = "world" };
        Thread.Sleep(80);
        yield return new PromptMessageEvent { Text = "Hello world" };
    }

    private IEnumerable<PromptEvent> Reasoning()
    {
        yield return new PromptThinkingEvent { Text = "let me " };
        Thread.Sleep(80);
        yield return new PromptThinkingEvent { Text = "think" };
        Thread.Sleep(80);
        yield return new PromptDeltaEvent { Text = "The " };
        Thread.Sleep(80);
        yield return new PromptDeltaEvent { Text = "answer" };
        Thread.Sleep(80);
        yield return new PromptMessageEvent { Text = "The answer" };
    }

    private PromptService Streaming(out Action<RemoteEvent> deliver)
    {
        Action<RemoteEvent>? handler = null;
        hub.Setup(x => x.Subscribe(It.IsAny<Action<RemoteEvent>>())).Callback<Action<RemoteEvent>>(registered => handler = registered).Returns(Mock.Of<IDisposable>());
        PromptService streaming = new(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object) { StreamInterval = TimeSpan.FromMilliseconds(20) };
        streaming.Configure(stopping.Token);
        deliver = remoteEvent => handler!(remoteEvent);
        return streaming;
    }

    [Fact]
    public async Task Prompt_StreamsTheMessageTheAiIsWritingInOrderBeforeItsFinishedFormOnlyWhileTheWorkspaceShowsTheWork()
    {
        Use(workspace with { ShowWork = true });
        PromptService streaming = Streaming(out _);
        prompter.OnCreate = session => session.ReplyWith(_ => Writing());

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !streaming.IsBusy(workspace.Id));

        List<RemoteEvent> published = [.. Events(RemoteEventKind.MessageAdded).Concat(Events(RemoteEventKind.MessageDelta))];
        int final = events.FindIndex(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageAdded && remoteEvent.Message!.Role == MessageRole.Assistant);
        string[] pieces = [.. Events(RemoteEventKind.MessageDelta).Select(remoteEvent => remoteEvent.Text!)];

        Assert.NotEmpty(pieces);
        Assert.Equal("Hello world", string.Concat(pieces));
        Assert.All(published.Where(remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta), remoteEvent => Assert.Equal(workspace.Id, remoteEvent.WorkspaceId));
        Assert.DoesNotContain(events.Skip(final + 1), remoteEvent => remoteEvent.Kind == RemoteEventKind.MessageDelta);
        Assert.Equal(["Hello world"], Messages(MessageRole.Assistant));
        Assert.Equal(["Hello world"], chats[workspace.Id].Where(message => message.Role == MessageRole.Assistant).Select(message => message.Text));
    }

    [Fact]
    public async Task Prompt_SendsNoPiecesWhileTheWorkspaceDoesNotShowTheWork()
    {
        PromptService streaming = Streaming(out _);
        prompter.OnCreate = session => session.ReplyWith(_ => Writing());

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !streaming.IsBusy(workspace.Id));

        Assert.Empty(Events(RemoteEventKind.MessageDelta));
        Assert.Equal(["Hello world"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task ShowWorkChanged_StartsAndStopsTheStreamingOfAPromptThatIsAlreadyRunning()
    {
        PromptService streaming = Streaming(out Action<RemoteEvent> deliver);
        using ManualResetEventSlim gate = new();
        prompter.OnCreate = session => session.ReplyWith(_ => Writing(gate));

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Task.Delay(200);
        Assert.Empty(Events(RemoteEventKind.MessageDelta));

        deliver(new RemoteEvent { Kind = RemoteEventKind.ShowWorkChanged, WorkspaceId = workspace.Id, ShowWork = true });
        gate.Set();
        await Until(() => !streaming.IsBusy(workspace.Id));

        Assert.Equal("lo world", string.Concat(Events(RemoteEventKind.MessageDelta).Select(remoteEvent => remoteEvent.Text)));
        Assert.Equal(["Hello world"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task Prompt_DropsThePiecesThatWereNotSentYetWhenTheMessageIsFinished()
    {
        Use(workspace with { ShowWork = true });
        PromptService streaming = Streaming(out _);
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptDeltaEvent { Text = "never sent" }, new PromptMessageEvent { Text = "done" }]);

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !streaming.IsBusy(workspace.Id));
        await Task.Delay(120);

        Assert.Empty(Events(RemoteEventKind.MessageDelta));
        Assert.Equal(["done"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task History_OfAWorkspaceWithoutAConversationStillSaysWhetherItShowsTheWork()
    {
        Use(workspace with { ShowWork = true, SessionId = null });

        ChatHistory history = await service.History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Empty(history.Messages);
        Assert.True(history.ShowWork);
    }

    [Fact]
    public async Task Attach_RefusesInvalidAndOversizedData()
    {
        await Assert.ThrowsAsync<RequestException>(() => service.Attach(workspace.Id, "a.txt", "not base64!", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => service.Attach(workspace.Id, "a.txt", Convert.ToBase64String(new byte[20 * 1024 * 1024 + 1]), CancellationToken.None));
    }

    [Fact]
    public async Task Prompt_TakesTheWorkspaceIntoWorkingPublishesTheConversationAndGivesItBackWhenTheAiFinishes()
    {
        prompter.OnCreate = session => session.Reply("hi there");

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Settled();

        Assert.Equal(["hello"], Messages(MessageRole.User));
        Assert.Equal(["hi there"], Messages(MessageRole.Assistant));
        Assert.All(Events(RemoteEventKind.MessageAdded), remoteEvent => Assert.InRange(remoteEvent.Message!.Time!.Value, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddSeconds(1)));
        Assert.Equal([true, false], Events(RemoteEventKind.WorkingChanged).Select(remoteEvent => remoteEvent.Working == true).ToList());
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.Equal(["hello"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        snapshots.Verify(x => x.Capture(It.IsAny<WorkspaceInfo>(), It.IsAny<CancellationToken>()), Times.Once);
        snapshots.Verify(x => x.Discard(workspace.Id), Times.Once);
    }

    [Fact]
    public async Task Prompt_PublishesTheReplyBeforeTheWorkspaceStopsCountingAsWorkingEvenWhenPublishingIsSlow()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        hub.Setup(x => x.Publish(It.Is<RemoteEvent>(remoteEvent => remoteEvent.Message != null && remoteEvent.Message.Role == MessageRole.Assistant))).Callback<RemoteEvent>(remoteEvent =>
        {
            Thread.Sleep(80);

            lock (events)
            {
                events.Add(remoteEvent);
            }
        });

        for (int number = 0; number < 3; number++)
        {
            int idle = number + 1;
            await service.Prompt(workspace.Id, $"q{number}", CancellationToken.None);
            await Until(() => Events(RemoteEventKind.WorkingChanged).Count(remoteEvent => remoteEvent.Working == false) == idle);
        }

        List<string> sequence;

        lock (events)
        {
            sequence = [.. events.Select(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged ? remoteEvent.Working == true ? "working" : "idle" : remoteEvent.Kind == RemoteEventKind.MessageAdded ? remoteEvent.Message!.Text : string.Empty).Where(item => item.Length > 0)];
        }

        string[] expected = [.. Enumerable.Range(0, 3).SelectMany(number => new[] { $"q{number}", "working", $"reply to q{number}", "idle" })];
        Assert.Equal(expected, sequence);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksIsSteeredIntoTheWorkAtOnceAndFollowedUpAfterwardsBecauseTheAiMayHaveMissedIt()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => Session.Prompts[0].Steered.Count == 1);

        Assert.Equal(["two"], Session.Prompts[0].Steered);
        Assert.Equal(WorkspaceState.Working, activity.Get(workspace.Id));
        Assert.Single(Session.Prompts);

        release.SetResult();
        await Settled();

        Assert.Equal(["one", "two"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        Assert.Contains("may have been missed", Session.Prompts[1].Notes.Single());
        Assert.Equal(["one", "two"], Messages(MessageRole.User));
        Assert.Equal(["first reply", "reply to two"], Messages(MessageRole.Assistant));
        Assert.Equal([true, false], Events(RemoteEventKind.WorkingChanged).Select(remoteEvent => remoteEvent.Working == true).ToList());
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        snapshots.Verify(x => x.Capture(It.IsAny<WorkspaceInfo>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksStaysWorkingUntilTheFollowUpIsDoneSoTheWorkspaceNeverReturnsToReadyWithInputOutstanding()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);

        release.SetResult();
        await Settled();

        List<string> order;

        lock (events)
        {
            order = [.. events.Select(remoteEvent => remoteEvent.Kind == RemoteEventKind.WorkingChanged ? remoteEvent.Working == true ? "working" : "idle" : remoteEvent.Kind == RemoteEventKind.MessageAdded ? remoteEvent.Message!.Text : string.Empty).Where(item => item.Length > 0)];
        }

        Assert.Equal(["one", "working", "two", "first reply", "reply to two", "idle"], order);
    }

    [Fact]
    public async Task Prompt_ThatIsOnlyForAnIdleAiIsRefusedWhileTheAiWorksAndSentOtherwise()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one", true, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "two", true, CancellationToken.None));

        Assert.Equal("The AI is working.", exception.Message);
        Assert.Empty(Session.Prompts[0].Steered);
        Assert.Equal(["one"], Messages(MessageRole.User));

        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "three", true, CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["one", "three"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Prompt_ThatIsOnlyForAnIdleAiLeavesNothingInTheChatWhenRefused()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one", true, CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);

        await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "two", true, CancellationToken.None));
        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["one", "first reply"], (await service.History(workspace.Id, null, null, null, CancellationToken.None)).Messages.Select(message => message.Text).ToList());
    }

    [Fact]
    public async Task Prompt_SentRightAsTheWorkFinishesIsNeverLostWhateverTheTiming()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        Random random = new(7);
        int count = 60;

        for (int number = 0; number < count; number++)
        {
            await service.Prompt(workspace.Id, $"p{number}", CancellationToken.None);

            if (random.Next(3) == 0)
            {
                await Task.Delay(random.Next(0, 4));
            }
        }

        await Until(() => !service.IsBusy(workspace.Id) && Events(RemoteEventKind.WorkingChanged).Count(remoteEvent => remoteEvent.Working == true) == Events(RemoteEventKind.WorkingChanged).Count(remoteEvent => remoteEvent.Working == false));

        List<string> expected = [.. Enumerable.Range(0, count).Select(number => $"p{number}")];
        Assert.Equal(expected, Messages(MessageRole.User));
        HashSet<string> worked = [.. Session.Prompts.SelectMany(prompt => prompt.Text.Split("\n\n"))];
        Assert.Equal(expected.Count, expected.Count(worked.Contains));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.False(Events(RemoteEventKind.WorkingChanged).Last().Working);
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksIsNotFollowedUpWhenTheConversationShowsItAsInputTheAiWroteAfter()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one and two" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one and two", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => Session.Prompts[0].Steered.Count == 1);

        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));
        await Task.Delay(300);

        Assert.Single(Session.Prompts);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_SentWhileTheAiWorksIsFollowedUpWhenTheConversationShowsItButTheAiWroteNothingAfter()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one and two" ? Silent(release.Task) : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one and two", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => Session.Prompts[0].Steered.Count == 1);

        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["one and two", "two"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        Assert.Contains("may have been missed", Session.Prompts[1].Notes.Single());
    }

    [Fact]
    public async Task Prompt_SentWhileLongWorkRunsIsNotFollowedUpHoweverManyEntriesTheAiWroteSince()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one and two" ? Busy(release.Task, 300) : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one and two", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => Session.Prompts[0].Steered.Count == 1);

        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));
        await Task.Delay(300);

        Assert.Single(Session.Prompts);
    }

    [Fact]
    public async Task History_ReadsAChunkByItsTextAsWellAsByItsMessageCountAndAlwaysAtLeastOneMessage()
    {
        Use(workspace with { ShowWork = true });
        DateTimeOffset now = DateTimeOffset.UtcNow;
        chats[workspace.Id] =
        [
            new ChatMessage { Role = MessageRole.User, Text = new string('a', 100), Time = now },
            new ChatMessage { Role = MessageRole.Assistant, Text = "Read a.txt", Time = now, IsWork = true },
            new ChatMessage { Role = MessageRole.Assistant, Text = "→ 3 lines", Time = now, IsWork = true },
            new ChatMessage { Role = MessageRole.Assistant, Text = new string('b', 15_000), Time = now },
            new ChatMessage { Role = MessageRole.Assistant, Text = new string('c', 15_000), Time = now },
            new ChatMessage { Role = MessageRole.Assistant, Text = new string('d', 50_000), Time = now }
        ];

        ChatHistory huge = await service.History(workspace.Id, null, null, 20_000, CancellationToken.None);
        ChatHistory next = await service.History(workspace.Id, huge.Older, null, 20_000, CancellationToken.None);
        ChatHistory last = await service.History(workspace.Id, next.Older, null, 20_000, CancellationToken.None);
        ChatHistory capped = await service.History(workspace.Id, null, 2, 1_000_000, CancellationToken.None);

        Assert.Equal([50_000], huge.Messages.Select(message => message.Text.Length));
        Assert.Equal("5", huge.Older);
        Assert.Equal([15_000], next.Messages.Select(message => message.Text.Length));
        Assert.Equal("4", next.Older);
        Assert.Equal([100, "Read a.txt".Length, "→ 3 lines".Length, 15_000], last.Messages.Select(message => message.Text.Length));
        Assert.Null(last.Older);
        Assert.Equal(2, capped.Messages.Count);
    }

    [Fact]
    public async Task Prompt_ReportsAWorkThatEndsWithoutAReplyOrAToolCallAndStillLeavesTheWorkspaceReady()
    {
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptThinkingEvent { Text = "hmm" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        string error = Assert.Single(Events(RemoteEventKind.Failed)).Error!;
        Assert.Contains("finished without writing a reply or using a tool", error);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    private PromptService Completing(IWorkCompletion completion)
    {
        PromptService completing = new(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object, null, null, null, () => completion);
        completing.Configure(stopping.Token);
        return completing;
    }

    [Theory]
    [InlineData("answer", true)]
    [InlineData("empty", false)]
    [InlineData("fail", false)]
    public async Task Prompt_TellsHowTheWorkEndedWhileTheWorkspaceIsStillTaken(string text, bool succeeded)
    {
        Mock<IWorkCompletion> completion = new();
        WorkspaceState during = WorkspaceState.Ready;
        completion.Setup(x => x.Complete(workspace.Id, It.IsAny<WorkResult>())).Returns(() =>
        {
            during = activity.Get(workspace.Id);
            return false;
        });
        service = Completing(completion.Object);
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "empty" ? [] : prompt.Text == "fail" ? [new PromptFailureEvent { Message = "boom" }] : [new PromptMessageEvent { Text = "fine" }]);

        await service.Prompt(workspace.Id, text, CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id) && activity.Get(workspace.Id) == WorkspaceState.Ready);

        completion.Verify(x => x.Complete(workspace.Id, It.Is<WorkResult>(result => (result.Answered && !result.Failed && !result.Stopped) == succeeded)), Times.Once);
        Assert.Equal(WorkspaceState.Working, during);
    }

    [Fact]
    public async Task Prompt_LeavesTheWorkspaceTakenForWhatCommitsItsChanges()
    {
        Mock<IWorkCompletion> completion = new();
        completion.Setup(x => x.Complete(workspace.Id, It.IsAny<WorkResult>())).Returns(() => activity.Pass(workspace.Id, WorkspaceState.Working, WorkspaceState.Managing));
        service = Completing(completion.Object);
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptMessageEvent { Text = "fine" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id) && completion.Invocations.Any(call => call.Method.Name == nameof(IWorkCompletion.Complete)) && activity.Get(workspace.Id) == WorkspaceState.Managing);
        await Task.Delay(50);

        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_StartsNothingWhileTheChangesAreBeingCommitted()
    {
        Mock<IWorkCompletion> completion = new();
        completion.Setup(x => x.IsCommitting(workspace.Id)).Returns(true);
        service = Completing(completion.Object);
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "go", CancellationToken.None));

        Assert.StartsWith("The changes of the workspace are being committed.", refused.Message);
        Assert.Empty(prompter.Sessions);
    }

    [Fact]
    public async Task Prompt_NotesTheShellCommandsOfASessionSoItsProcessesCanBeToldApart()
    {
        Mock<IShellCommandLog> commands = new();
        service = new PromptService(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object, null, null, commands.Object);
        service.Configure(stopping.Token);
        prompter.OnCreate = session => session.ReplyWith(_ =>
        [
            new PromptToolCallEvent { Id = "1", Name = "shell", Input = "{\"command\":\"make all\",\"background\":true}" },
            new PromptToolCallEvent { Id = "2", Name = "shell", Input = "not json" },
            new PromptToolCallEvent { Id = "3", Name = "read_file", Input = "{\"command\":\"not one\"}" },
            new PromptMessageEvent { Text = "done" }
        ]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        commands.Verify(x => x.Note(workspace.Id, "make all"), Times.Once);
        commands.Verify(x => x.Note(It.IsAny<Guid>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_ReportsNothingForAWorkWhoseAnswerIsOnlyAToolCallAndItsResult()
    {
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptToolCallEvent { Id = "1", Name = "read_file", Input = "{}" }, new PromptToolResultEvent { Id = "1", Output = "text", IsError = false }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Empty(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Prompt_ReportsAnEndWithoutAReplyOnlyForTheWorkThatHadNoneAndNotForTheNext()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "empty" ? [] : [new PromptMessageEvent { Text = "fine" }]);

        await service.Prompt(workspace.Id, "empty", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "answered", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Interrupt_EndsTheWorkWithoutReportingThatTheAiGaveNoReply()
    {
        prompter.OnCreate = session => session.Hang();
        await service.Prompt(workspace.Id, "long", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);

        await service.Interrupt(workspace.Id);
        await Task.Delay(200);

        Assert.False(service.IsBusy(workspace.Id));
        Assert.Empty(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Prompt_StreamsTheReasoningAsItsOwnPassageAndKeepsItAsATimestampedWorkMessageBeforeTheAnswer()
    {
        Use(workspace with { ShowWork = true });
        PromptService streaming = Streaming(out _);
        prompter.OnCreate = session => session.ReplyWith(_ => Reasoning());
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !streaming.IsBusy(workspace.Id));

        RemoteEvent[] pieces = [.. Events(RemoteEventKind.MessageDelta)];
        Assert.Equal("let me think", string.Concat(pieces.Where(piece => piece.Thought == true).Select(piece => piece.Text)));
        Assert.Equal("The answer", string.Concat(pieces.Where(piece => piece.Thought != true).Select(piece => piece.Text)));
        ChatMessage thought = Assert.Single(chats[workspace.Id], message => message.IsThought);
        Assert.Equal(("let me think", true, MessageRole.Assistant), (thought.Text, thought.IsWork, thought.Role));
        Assert.InRange(thought.Time!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(["go", "let me think", "The answer"], chats[workspace.Id].Select(message => message.Text));
        Assert.Equal(["The answer"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task Prompt_KeepsTheReasoningEvenWhileTheWorkspaceDoesNotShowTheWorkSoShowingItLaterBringsItBack()
    {
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptThinkingEvent { Text = "step one. " }, new PromptThinkingEvent { Text = "step two." }, new PromptToolCallEvent { Id = "1", Name = "read_file", Input = "{\"path\":\"a.txt\"}" }, new PromptMessageEvent { Text = "done" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Empty(Events(RemoteEventKind.MessageDelta));
        Assert.Equal(["go", "step one. step two.", "Read a.txt", "done"], chats[workspace.Id].Select(message => message.Text));
        Assert.Equal([false, true, true, false], chats[workspace.Id].Select(message => message.IsWork));
        Assert.Equal([false, true, false, false], chats[workspace.Id].Select(message => message.IsThought));
        Assert.Equal(["go", "done"], (await service.History(workspace.Id, null, null, null, CancellationToken.None)).Messages.Select(message => message.Text));
    }

    [Fact]
    public async Task Prompt_EndsTheReasoningWhenTheWorkEndsWithoutAnythingElse()
    {
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptThinkingEvent { Text = "hmm" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        await Until(() => chats.TryGetValue(workspace.Id, out List<ChatMessage>? written) && written.Any(message => message.IsThought));

        Assert.Equal("hmm", Assert.Single(chats[workspace.Id], message => message.IsThought).Text);
    }

    [Fact]
    public async Task Prompt_SendsNoReasoningWhileTheWorkspaceDoesNotShowTheWork()
    {
        PromptService streaming = Streaming(out _);
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptThinkingEvent { Text = "let me think" }, new PromptMessageEvent { Text = "done" }]);

        await streaming.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !streaming.IsBusy(workspace.Id));

        Assert.Empty(Events(RemoteEventKind.MessageDelta));
        Assert.Empty(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Prompt_ShowsWhatTheToolDoesOnItsOwnAsAWorkMessage()
    {
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptNoticeEvent { Text = "The context window is full." }, new PromptMessageEvent { Text = "done" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        ChatMessage notice = Assert.Single(Events(RemoteEventKind.MessageAdded).Select(remoteEvent => remoteEvent.Message!), message => message.IsWork);
        Assert.Equal("The context window is full.", notice.Text);
        Assert.Equal(MessageRole.Assistant, notice.Role);
    }

    [Fact]
    public async Task Interrupt_ForgetsTheInputSteeredIntoTheWorkButNotTheOnesSentAfterwards()
    {
        prompter.OnCreate = session => session.Hang().Reply("fine");
        await service.Prompt(workspace.Id, "long", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "waiting", CancellationToken.None);

        await service.Interrupt(workspace.Id);
        await Task.Delay(300);

        Assert.Single(Session.Prompts);
        Assert.False(service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "again", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        Assert.Equal(["long", "again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task Prompt_IsRefusedWhileTheWorkIsBeingStopped()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Working);
        activity.Halt(workspace.Id, WorkspaceState.Stopping);

        await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "again", CancellationToken.None));

        Assert.Empty(prompter.Sessions);
        Assert.Equal(WorkspaceState.Stopping, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_DuringAGitActionWorksWithoutChangingTheState()
    {
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        prompter.OnCreate = session => session.Reply("resolved");

        await service.Prompt(workspace.Id, "resolve the conflicts", CancellationToken.None);
        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(WorkspaceState.Managing, activity.Get(workspace.Id));
        snapshots.Verify(x => x.Capture(It.IsAny<WorkspaceInfo>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prompt_RejectsEmptyText() => await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "  ", CancellationToken.None));

    private IPromptSession Existing()
    {
        ClaudeSetup setup = new() { WorkspacePath = workspace.Folder };
        return prompter.Create(setup).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Prompt_ThatTookTheWorkspaceButNeverStartedSaysSoToWhatFollowsTheWork()
    {
        Mock<IWorkCompletion> completion = new();
        PromptService followed = new(workspaces.Object, store.Object, host.Object, hub.Object, activity, snapshots.Object, null, null, null, () => completion.Object);
        followed.Configure(stopping.Token);
        Broken("The tool is missing.");
        int taken = 0;

        await Assert.ThrowsAsync<RequestException>(() => followed.PromptWhenIdle(workspace.Id, "hello", () => taken++, CancellationToken.None));

        Assert.Equal(1, taken);
        completion.Verify(x => x.Abort(workspace.Id), Times.Once);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));

        // One that never had the workspace turns nothing off and takes nothing.
        activity.TryEnter(workspace.Id, WorkspaceState.Managing);
        await Assert.ThrowsAsync<RequestException>(() => followed.PromptWhenIdle(workspace.Id, "hello", () => taken++, CancellationToken.None));

        Assert.Equal(1, taken);
        completion.Verify(x => x.Abort(workspace.Id), Times.Once);
    }

    [Fact]
    public async Task Prompt_AddsWhatTheAiHasToKnowToWhatItIsSentButNotToTheChat()
    {
        Mock<IWorkCompletion> completion = new();
        completion.Setup(x => x.Brief(workspace.Id, "go")).Returns("[Auto Remote]\nThis work runs unattended.");
        service = Completing(completion.Object);
        prompter.OnCreate = session => session.ReplyWith(_ => [new PromptMessageEvent { Text = "fine" }]);

        await service.Prompt(workspace.Id, "go", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id) && Messages(MessageRole.Assistant).Count == 1);

        Assert.Equal("go\n\n[Auto Remote]\nThis work runs unattended.", Assert.Single(Session.Prompts).Text);
        Assert.Equal(["go"], Messages(MessageRole.User));
        completion.Verify(x => x.Began(workspace.Id, "go"), Times.Once);
        completion.Verify(x => x.Complete(workspace.Id, It.Is<WorkResult>(result => result.Reply == "fine")), Times.Once);
    }

    [Fact]
    public async Task SetSettings_PutsTheSandboxOnTheSessionAndKeepsItWithTheWorkspace()
    {
        WorkspaceSettings current = await service.GetSettings(workspace.Id, CancellationToken.None);
        Assert.False(current.IsSandboxed);
        Assert.True(Session.Configuration.IsShellAllowed);
        Assert.True(Session.Configuration.IsSystemAccessAllowed);

        WorkspaceSettings saved = await service.SetSettings(workspace.Id, current with { IsSandboxed = true }, CancellationToken.None);

        Assert.True(saved.IsSandboxed);
        Assert.False(Session.Configuration.IsShellAllowed);
        Assert.False(Session.Configuration.IsSystemAccessAllowed);
    }

    private void Broken(string message) => host.Mock.Setup(x => x.CreateSession(It.IsAny<string>(), It.IsAny<AiService>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException(message));

    [Fact]
    public async Task Prompt_MakesASessionOnTheWorkspacesProviderInItsFolderAndAttachesItsIdentifierWhenTheWorkspaceHasNone()
    {
        prompter.OnCreate = session => session.Reply("ok");
        Use(workspace with { Service = AiService.Codex });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        host.Mock.Verify(x => x.CreateSession(workspace.Folder, AiService.Codex, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsType<CodexSetup>(Assert.Single(prompter.Created));
        workspaces.Verify(x => x.AttachSession(workspace.Id, Session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_RefusesASessionThatCannotBeMadeWithItsMessageAndLeavesTheWorkspaceReady()
    {
        Broken("Local models need a model for every capability.");

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Contains("Local models need a model", exception.Message);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task GetSettings_ReadsHowTheSessionRunsAndMakesTheSessionWhenThereIsNone()
    {
        WorkspaceSettings settings = await service.GetSettings(workspace.Id, CancellationToken.None);

        Assert.Equal((AiService.ClaudeCode, AiCapability.Balanced), (settings.Service, settings.Capability));
        Assert.Equal("Fake", settings.Descriptors["Provider"]);
        Assert.Equal(Session.Id, workspace.SessionId);
        workspaces.Verify(x => x.AttachSession(workspace.Id, Session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetSettings_GivesTheSessionEveryValueAndAnswersHowItNowRuns()
    {
        WorkspaceSettings wanted = new() { Service = AiService.Codex, Capability = AiCapability.Max, Effort = AiEffort.Low, IsSandboxed = true, IsWebAllowed = false, IsImageGenerationAllowed = true, Instructions = "  be brief  " };

        WorkspaceSettings saved = await service.SetSettings(workspace.Id, wanted, CancellationToken.None);

        IPromptSessionConfiguration configuration = Session.Configuration;
        Assert.Equal((PromptCapability.Max, PromptEffort.Low), (configuration.Capability, configuration.Effort));
        Assert.Equal((false, false, false, true), (configuration.IsShellAllowed, configuration.IsSystemAccessAllowed, configuration.IsWebAllowed, configuration.IsImageGenerationAllowed));
        Assert.Equal(host.BuiltIn.Compose("be brief", 5), configuration.Instructions);
        Assert.Equal("be brief", saved.Instructions);
        Assert.Equal((AiService.ClaudeCode, AiCapability.Max, AiEffort.Low), (saved.Service, saved.Capability, saved.Effort));
    }

    [Fact]
    public async Task SettingsCarryTheReviewAndExpandPromptsOfTheWorkspaceNextToTheGlobalOnes()
    {
        host.Mock.Setup(x => x.Get()).Returns(new AiEnvironment { ReviewPrompt = "global review", ExpandPrompt = "global expand" });
        WorkspaceSettings none = await service.GetSettings(workspace.Id, CancellationToken.None);

        WorkspaceSettings own = await service.SetSettings(workspace.Id, none with { ReviewPrompt = "  my review ", ExpandPrompt = "my expand" }, CancellationToken.None);
        WorkspaceSettings cleared = await service.SetSettings(workspace.Id, own with { ReviewPrompt = null, ExpandPrompt = "   " }, CancellationToken.None);

        Assert.Equal((null, null, "global review", "global expand"), (none.ReviewPrompt, none.ExpandPrompt, none.GlobalReviewPrompt, none.GlobalExpandPrompt));
        Assert.Equal(("my review", "my expand", "global review", "global expand"), (own.ReviewPrompt, own.ExpandPrompt, own.GlobalReviewPrompt, own.GlobalExpandPrompt));
        Assert.Equal((null, null), (cleared.ReviewPrompt, cleared.ExpandPrompt));
        workspaces.Verify(x => x.SetOverrides(workspace.Id, null, "   ", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OpeningASession_GivesTheAiTheInstructionsForTheInterval()
    {
        await service.GetSettings(workspace.Id, CancellationToken.None);

        Assert.Empty(Session.Transient.Tools);
        Assert.Equal(host.BuiltIn.Compose(null, 5), Session.Configuration.Instructions);
    }

    [Fact]
    public async Task TheInstructionsFollowTheIntervalOfTheWorkspaceOrTheEnvironmentAndTheWorkspaceCanClearItsOwn()
    {
        host.Mock.Setup(x => x.Get()).Returns(new AiEnvironment { StatusMinutes = 12 });
        WorkspaceSettings first = await service.GetSettings(workspace.Id, CancellationToken.None);

        WorkspaceSettings own = await service.SetSettings(workspace.Id, first with { StatusMinutes = 3 }, CancellationToken.None);
        string withOwn = Session.Configuration.Instructions!;
        WorkspaceSettings cleared = await service.SetSettings(workspace.Id, own with { StatusMinutes = null }, CancellationToken.None);

        Assert.Equal((null, 12), (first.StatusMinutes, first.GlobalStatusMinutes));
        Assert.Equal((3, 12), (own.StatusMinutes, own.GlobalStatusMinutes));
        Assert.Equal(host.BuiltIn.Compose(null, 3), withOwn);
        Assert.Null(cleared.StatusMinutes);
        workspaces.Verify(x => x.SetOverrides(workspace.Id, It.IsAny<string?>(), It.IsAny<string?>(), 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public async Task SetSettings_RefusesAStatusIntervalOutOfRange(int minutes)
    {
        WorkspaceSettings current = await service.GetSettings(workspace.Id, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => service.SetSettings(workspace.Id, current with { StatusMinutes = minutes }, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshInstructions_GivesOpenSessionsTheNewGlobalIntervalUnlessTheWorkspaceHasItsOwn()
    {
        host.Mock.Setup(x => x.Get()).Returns(new AiEnvironment { StatusMinutes = 5 });
        await service.GetSettings(workspace.Id, CancellationToken.None);
        Assert.Equal(host.BuiltIn.Compose(null, 5), Session.Configuration.Instructions);
        host.Mock.Setup(x => x.ComposeInstructions(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<string?>())).Returns((string? user, int? minutes, string? note) => host.BuiltIn.Compose(user, minutes ?? 20, note));

        await service.RefreshInstructions(CancellationToken.None);

        Assert.Equal(host.BuiltIn.Compose(null, 20), Session.Configuration.Instructions);

        Use(workspace with { StatusMinutes = 7 });
        await service.RefreshInstructions(CancellationToken.None);

        Assert.Equal(host.BuiltIn.Compose(null, 7), Session.Configuration.Instructions);
    }

    [Fact]
    public async Task SetSettings_TurnsBlankInstructionsIntoNone()
    {
        WorkspaceSettings current = await service.GetSettings(workspace.Id, CancellationToken.None);

        WorkspaceSettings saved = await service.SetSettings(workspace.Id, current with { Instructions = "   " }, CancellationToken.None);

        Assert.Null(saved.Instructions);
        Assert.Equal(host.BuiltIn.Compose(null, 5), Session.Configuration.Instructions);
    }

    [Fact]
    public async Task TheBuiltInInstructionsGoWithEverySessionAndAreNeverShownToTheUser()
    {
        IPromptSession existing = Existing();
        existing.Configuration.Instructions = "my own";
        await existing.DisposeAsync();
        Use(workspace with { SessionId = existing.Id });

        WorkspaceSettings first = await service.GetSettings(workspace.Id, CancellationToken.None);

        Assert.Equal("my own", first.Instructions);
        Assert.Equal(host.BuiltIn.Compose("my own", 5), prompter.Sessions[^1].Configuration.Instructions);
    }

    [Fact]
    public async Task ASessionThatAlreadyHoldsOnlyTheBuiltInInstructionsShowsNoneAndIsLeftAsItIs()
    {
        IPromptSession existing = Existing();
        existing.Configuration.Instructions = host.BuiltIn.Compose(null, 5);
        await existing.DisposeAsync();
        Use(workspace with { SessionId = existing.Id });

        WorkspaceSettings settings = await service.GetSettings(workspace.Id, CancellationToken.None);

        Assert.Null(settings.Instructions);
        Assert.Equal(host.BuiltIn.Compose(null, 5), prompter.Sessions[^1].Configuration.Instructions);
    }

    [Fact]
    public async Task SetSettings_RefusesAValueTheAiDoesNotKnow()
    {
        await service.GetSettings(workspace.Id, CancellationToken.None);

        await Assert.ThrowsAsync<RequestException>(() => service.SetSettings(workspace.Id, new WorkspaceSettings { Capability = (AiCapability)99 }, CancellationToken.None));

        Assert.Equal(PromptCapability.Balanced, Session.Configuration.Capability);
    }

    [Fact]
    public async Task SetSettings_ReachesTheSessionAgainWhenTheIdleSessionIsClosedAndOpenedAgain()
    {
        await service.SetSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Strong, Effort = AiEffort.Max }, CancellationToken.None);

        WorkspaceSettings again = await service.GetSettings(workspace.Id, CancellationToken.None);

        Assert.Equal((AiCapability.Strong, AiEffort.Max), (again.Capability, again.Effort));
        Assert.Single(prompter.Sessions);
    }

    [Fact]
    public async Task SetSettings_AppliesToTheNextPromptOfTheSession()
    {
        prompter.OnCreate = session => session.Reply("ok");
        await service.SetSettings(workspace.Id, new WorkspaceSettings { Capability = AiCapability.Fast, Effort = AiEffort.Medium }, CancellationToken.None);

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal((PromptCapability.Fast, PromptEffort.Medium), (Session.Prompts.Single().Capability, Session.Prompts.Single().Effort));
    }

    [Fact]
    public async Task Prompt_ResumesTheAttachedLiveSessionAndKeepsItForTheNextPrompt()
    {
        IPromptSession existing = Existing();
        ((FakePromptSession)existing).ReplyWith(_ => [new PromptMessageEvent { Text = "again" }]);
        await existing.DisposeAsync();
        Use(workspace with { SessionId = existing.Id });

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(["one", "two"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        workspaces.Verify(x => x.AttachSession(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prompt_StartsANewSessionWhenTheStoredIdentifierIsOneAnOlderServerWrote()
    {
        string legacy = Guid.NewGuid().ToString();
        prompter.OnCreate = session => session.Reply("ok");
        Use(workspace with { SessionId = legacy });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(Session.Id, workspace.SessionId);
    }

    [Fact]
    public async Task Prompt_StartsANewSessionWhenTheStoredOneIsGone()
    {
        prompter.OnCreate = session => session.Reply("ok");
        Use(workspace with { SessionId = Guid.NewGuid().ToString("N") });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(Session.Id, workspace.SessionId);
    }

    [Fact]
    public async Task Prompt_MovesTheLoadedSessionToTheCurrentFolderWhenTheWorkspaceWasRenamed()
    {
        string renamed = Path.Combine(temp.Path, "renamed");
        Directory.CreateDirectory(renamed);
        IPromptSession existing = Existing();
        ((FakePromptSession)existing).ReplyWith(_ => [new PromptMessageEvent { Text = "ok" }]);
        await existing.DisposeAsync();
        Use(workspace with { SessionId = existing.Id, Folder = renamed });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(Path.GetFullPath(renamed), Path.GetFullPath(Session.WorkspacePath));
        Assert.Equal(existing.Id, workspace.SessionId);
        Assert.Empty(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Prompt_SendsImagesAttachedToTheMessageToTheAi()
    {
        string folder = Path.Combine(Path.GetTempPath(), "prompt-service-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        try
        {
            string image = Path.Combine(folder, "pic.png");
            string text = Path.Combine(folder, "notes.txt");
            await File.WriteAllBytesAsync(image, [1, 2, 3]);
            await File.WriteAllTextAsync(text, "notes");
            prompter.OnCreate = session => session.Reply("seen");

            await service.Prompt(workspace.Id, $"look\nAttached file: {image}\nAttached file: {text}", CancellationToken.None);
            await Until(() => !service.IsBusy(workspace.Id));

            Assert.Equal(["pic.png"], Session.Prompts.Single().Attachments.Select(attachment => attachment.Name).ToList());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Prompt_KeepsFollowingTheAiWhenPublishingOneOfItsMessagesFails()
    {
        prompter.OnCreate = session => session.Reply(new PromptMessageEvent { Text = "boom" }, new PromptMessageEvent { Text = "after" });
        hub.Setup(x => x.Publish(It.Is<RemoteEvent>(remoteEvent => remoteEvent.Message != null && remoteEvent.Message.Text == "boom"))).Throws(new InvalidOperationException("subscriber failed"));

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["after"], Messages(MessageRole.Assistant));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_StampsTheFailuresItPublishesWithTheTimeTheyHappened()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        prompter.OnCreate = session => session.Fail("boom");

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        RemoteEvent failure = Events(RemoteEventKind.Failed).Single();
        Assert.Equal("boom", failure.Error);
        Assert.InRange(failure.Time!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Prompt_ClosesTheSessionItMadeWhenItCannotBeAttachedToTheWorkspace()
    {
        workspaces.Setup(x => x.AttachSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

        await Assert.ThrowsAsync<IOException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.True(Session.IsDisposed);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Failure_IsReportedEndsTheWorkDropsThePromptsWaitingAndTheNextPromptStartsAgain()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Failing(release.Task) : [new PromptMessageEvent { Text = "fine" }]);

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "waiting", CancellationToken.None);
        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));
        await Task.Delay(200);
        Assert.Single(Session.Prompts);

        Assert.Equal(["boom"], Events(RemoteEventKind.Failed).Select(remoteEvent => remoteEvent.Error).ToList());
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));

        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["fine"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task Failure_OfAToolThatCannotBeStartedNamesTheService()
    {
        prompter.OnCreate = session => session.Fail(new PromptToolStartException("not found"));

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Contains("Claude Code could not be started", Events(RemoteEventKind.Failed).Single().Error);
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Prompt_WhenTheSessionCannotBeOpenedFailsAndLeavesTheWorkspaceReady()
    {
        Broken("The workspace folder does not exist.");

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Contains("does not exist", exception.Message);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        snapshots.Verify(x => x.Discard(workspace.Id), Times.Once);
    }

    [Theory]
    [InlineData(SideSessionState.Integrating)]
    [InlineData(SideSessionState.Deleting)]
    public async Task Prompt_OfASideSessionThatIsNotReadyIsRefusedWhereverItComesFrom(SideSessionState state)
    {
        workspaces.Setup(x => x.GetSide(workspace.Id)).Returns(new StoredSideSession { ParentId = Guid.NewGuid(), Name = "s", Service = AiService.ClaudeCode, State = state, StartedFrom = SideSessionStart.Commit, Root = "/r", Folder = "/r/f" });

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "resume unfinished work", true, CancellationToken.None));

        Assert.Contains("not ready", exception.Message);
        Assert.Empty(prompter.Sessions.SelectMany(session => session.Prompts));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
    }

    [Fact]
    public async Task Interrupt_StopsTheWorkKeepsTheSessionAndReportsNoFailure()
    {
        prompter.OnCreate = session => session.Hang().Reply("after");
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        Assert.True(service.IsBusy(workspace.Id));

        await service.Interrupt(workspace.Id);

        Assert.False(service.IsBusy(workspace.Id));
        Assert.Empty(Events(RemoteEventKind.Failed));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.Single(Session.Prompts);
        Assert.False(Session.IsDisposed);

        await service.Prompt(workspace.Id, "next", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["long job", "next"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        Assert.Equal(["after"], Messages(MessageRole.Assistant));
    }

    private PromptToolCallEvent Command() => new() { Id = "1", Name = "shell", Input = "{\"command\":\"sleep 100\"}" };

    private (ManualResetEventSlim Release, ManualResetEventSlim Reached) RunCommand(bool ending)
    {
        ManualResetEventSlim release = new();
        ManualResetEventSlim reached = new();
        IPromptSession existing = Existing();
        ((FakePromptSession)existing).ReplyWith(prompt => prompt.Text == "long job" ? Running(release, reached, ending) : [new PromptMessageEvent { Text = "after" }]);
        Use(workspace with { SessionId = existing.Id });
        return (release, reached);
    }

    private IEnumerable<PromptEvent> Running(ManualResetEventSlim release, ManualResetEventSlim reached, bool ending)
    {
        yield return Command();

        if (ending)
        {
            yield return new PromptToolResultEvent { Id = "1", Output = "done" };
        }

        reached.Set();
        release.Wait(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Interrupt_WithAShellCommandStillRunningClosesTheSessionSoTheCommandEndsAndTheNextPromptReopensIt()
    {
        (ManualResetEventSlim release, ManualResetEventSlim reached) = RunCommand(false);
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        FakePromptSession first = Session;
        await Until(() => reached.IsSet);

        await service.Interrupt(workspace.Id);
        release.Set();

        Assert.True(first.IsDisposed);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Empty(Events(RemoteEventKind.Failed));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));

        await service.Prompt(workspace.Id, "next", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(["after"], Messages(MessageRole.Assistant));
    }

    [Fact]
    public async Task Interrupt_AfterTheShellCommandEndedKeepsTheSessionOpen()
    {
        (ManualResetEventSlim release, ManualResetEventSlim reached) = RunCommand(true);
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => reached.IsSet);

        await service.Interrupt(workspace.Id);
        release.Set();

        Assert.False(Session.IsDisposed);
        Assert.False(service.IsBusy(workspace.Id));
    }

    [Fact]
    public async Task Close_EndsTheWorkAndClosesEverySessionWithoutReportingAFailure()
    {
        prompter.OnCreate = session => session.Hang();
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => prompter.Sessions.Count == 1 && Session.Prompts.Count == 1);

        await service.Close();

        Assert.True(Session.IsDisposed);
        Assert.Empty(Events(RemoteEventKind.Failed));
    }

    [Fact]
    public async Task Interrupt_WithoutASessionDoesNothing()
    {
        await service.Interrupt(workspace.Id);
        await service.Interrupt(Guid.NewGuid());

        Assert.Empty(Events(RemoteEventKind.WorkingChanged));
    }

    [Fact]
    public async Task Release_EndsOnlyAnIdleSession()
    {
        prompter.OnCreate = session => session.Hang();
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => prompter.Sessions.Count == 1 && Session.Prompts.Count == 1);

        await service.Release(workspace.Id);
        Assert.False(Session.IsDisposed);

        await service.Interrupt(workspace.Id);
        await service.Release(workspace.Id);

        Assert.True(Session.IsDisposed);
    }

    [Fact]
    public async Task Forget_EndsTheSessionAndDeletesTheConversationOnRequest()
    {
        prompter.OnCreate = session => session.Reply("ok");
        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        FakePromptSession session = Session;

        await service.Forget(workspace, true);

        Assert.True(session.IsDisposed);
        Assert.Empty(prompter.Sessions);
    }

    [Fact]
    public async Task Forget_KeepsTheConversationWhenAskedTo()
    {
        prompter.OnCreate = session => session.Reply("ok");
        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        await service.Forget(workspace, false);

        Assert.True(Session.IsDisposed);
        Assert.Single(prompter.Sessions);
    }

    [Fact]
    public async Task History_ReturnsTheNewestChunkThenEarlierOnesByCursor()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);

        for (int number = 1; number <= 3; number++)
        {
            await service.Prompt(workspace.Id, $"q{number}", CancellationToken.None);
            await Until(() => !service.IsBusy(workspace.Id));
        }

        ChatHistory newest = await service.History(workspace.Id, null, 4, null, CancellationToken.None);
        Assert.Equal(["q2", "reply to q2", "q3", "reply to q3"], newest.Messages.Select(message => message.Text).ToList());
        Assert.NotNull(newest.Older);

        ChatHistory earlier = await service.History(workspace.Id, newest.Older, 4, null, CancellationToken.None);
        Assert.Equal(["q1", "reply to q1"], earlier.Messages.Select(message => message.Text).ToList());
        Assert.Null(earlier.Older);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], earlier.Messages.Select(message => message.Role).ToList());
        Assert.All(newest.Messages.Concat(earlier.Messages), message => Assert.NotNull(message.Time));
    }

    [Fact]
    public async Task History_HoldsOnlyWhatWasSaidSoNothingTheToolsDidAppearsAndTheAiStaysIdle()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text.StartsWith("tools", StringComparison.Ordinal)
            ? [new PromptToolCallEvent { Id = "1", Name = "shell", Input = "{}" }, new PromptToolResultEvent { Id = "1", Output = "done" }, new PromptMessageEvent { Text = "used tools" }]
            : [new PromptMessageEvent { Text = "ok" }]);
        await service.Prompt(workspace.Id, "tools please", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "[Request interrupted by user]", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        ChatHistory history = await service.History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Equal(["tools please", "used tools", "[Request interrupted by user]", "ok"], history.Messages.Select(message => message.Text).ToList());
        Assert.False(history.Working);
        Assert.Null(history.Older);
    }

    [Fact]
    public async Task History_IsKeptByTheServerAcrossARestartWithoutReadingTheAiServiceAgain()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "q1", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        int sessions = prompter.Sessions.Count;

        ChatHistory history = await Restart().History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Equal(["q1", "reply to q1"], history.Messages.Select(message => message.Text).ToList());
        Assert.Equal(sessions, prompter.Sessions.Count);
        store.Verify(x => x.SaveMessages(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task History_TakesOverTheAiServicesConversationOnceWithoutSummariesNotesOrReplays()
    {
        FakePromptSession seeded = (FakePromptSession)Existing();
        seeded.ReplyWith(prompt => prompt.Text.StartsWith("<system-reminder>", StringComparison.Ordinal) ? [] : [new PromptMessageEvent { Text = "reply to " + prompt.Text[..Math.Min(prompt.Text.Length, 12)] }]);

        foreach (string text in new[] { "first question", "This session is being continued from a previous conversation that ran out of context. Summary: x", "<system-reminder>\nNote from the host application: may have been missed\n</system-reminder>", "steered text", "last question" })
        {
            await seeded.Send(text);
            await Until(() => seeded.State == PromptSessionState.Waiting);
        }

        await seeded.DisposeAsync();
        Use(workspace with { SessionId = seeded.Id });

        ChatHistory history = await service.History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Equal(["first question", "reply to first questi", "reply to This session", "reply to steered text", "last question", "reply to last questio"], history.Messages.Select(message => message.Text).ToList());
        await service.History(workspace.Id, null, null, null, CancellationToken.None);
        store.Verify(x => x.SaveMessages(workspace.Id, It.IsAny<IReadOnlyList<ChatMessage>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task History_DoesNotKeepWhatTheServerSendsAgainAsAFollowUpOrAsksTheAiInAFork()
    {
        TaskCompletionSource release = new();
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text == "one" ? Slow(release.Task, "first reply") : [new PromptMessageEvent { Text = "reply to " + prompt.Text }]);
        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => Session.Prompts.Count == 1);
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        release.SetResult();
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Ask(workspace.Id, "write a message", CancellationToken.None);

        ChatHistory history = await service.History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Equal(["one", "two", "first reply", "reply to two"], history.Messages.Select(message => message.Text).ToList());
    }

    [Fact]
    public async Task History_WithoutAConversationIsEmptyAndANonsenseCursorIsRefused()
    {
        ChatHistory history = await service.History(workspace.Id, null, null, null, CancellationToken.None);

        Assert.Empty(history.Messages);
        Assert.Null(history.Older);
        Assert.Empty(prompter.Sessions);
        await Assert.ThrowsAsync<RequestException>(() => service.History(workspace.Id, "nonsense", null, null, CancellationToken.None));
    }

    private IEnumerable<PromptEvent> Failing(Task gate)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptFailureEvent { Message = "boom" }];
    }

    private IEnumerable<PromptEvent> Silent(Task gate)
    {
        gate.GetAwaiter().GetResult();
        return [];
    }

    private IEnumerable<PromptEvent> Busy(Task gate, int messages)
    {
        gate.GetAwaiter().GetResult();
        return [.. Enumerable.Range(0, messages).Select(number => new PromptMessageEvent { Text = "step " + number })];
    }

    private IEnumerable<PromptEvent> Slow(Task gate, string text)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptMessageEvent { Text = text }];
    }

    [Fact]
    public async Task Ask_AnswersInAForkWithoutTouchingTheConversationOrTheWorkingState()
    {
        prompter.OnCreate = session => session.Reply("Fix the timeout");

        string answer = await service.Ask(workspace.Id, "write a message", CancellationToken.None);

        Assert.Equal("Fix the timeout", answer);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.Empty(Session.Prompts);
        Assert.Empty(Events(RemoteEventKind.MessageAdded));
    }

    [Fact]
    public async Task Ask_RefusesAnEmptyQuestionAndReportsAFailingAi()
    {
        prompter.OnCreate = session => session.Fail(new PromptSessionException("no key"));

        await Assert.ThrowsAsync<RequestException>(() => service.Ask(workspace.Id, " ", CancellationToken.None));

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Ask(workspace.Id, "write a message", CancellationToken.None));
        Assert.Contains("no key", exception.Message);
    }
}
