namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class PromptServiceTests : IDisposable
{
    private readonly TempFolder temp = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly FakePromptProvider prompter = new();

    private readonly FakePromptProvider codex = new();

    private readonly Mock<IEventHub> hub = new();

    private readonly List<RemoteEvent> events = [];

    private readonly WorkspaceActivity activity;

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly CancellationTokenSource stopping = new();

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
        activity = new WorkspaceActivity(hub.Object);
        service = new PromptService(workspaces.Object, store.Object, (_, ai) => ai == AiService.Codex ? codex : prompter, hub.Object, activity, snapshots.Object);
        Use(workspace);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);
    }

    public void Dispose() => temp.Dispose();

    private FakePromptSession Session => prompter.Sessions[^1];

    private void Use(WorkspaceInfo info)
    {
        workspace = info;
        workspaces.Setup(x => x.Get(workspace.Id, It.IsAny<CancellationToken>())).ReturnsAsync(() => workspace);
        workspaces.Setup(x => x.AttachAiSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).Callback<Guid, string, CancellationToken>((_, session, _) => workspace = workspace with { AiSessionId = session }).Returns(Task.CompletedTask);
    }

    private PromptService Restart()
    {
        service = new PromptService(workspaces.Object, store.Object, (_, ai) => ai == AiService.Codex ? codex : prompter, hub.Object, activity, snapshots.Object);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);
        return service;
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 150 && !condition(); attempt++)
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

    private List<string> Messages(MessageRole role) => [.. Events(RemoteEventKind.MessageAdded).Where(remoteEvent => remoteEvent.Message!.Role == role).Select(remoteEvent => remoteEvent.Message!.Text)];

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
    public async Task Prompt_TakesTheWorkspaceIntoWorkingPublishesTheConversationAndGivesItBackWhenTheAiFinishes()
    {
        prompter.OnCreate = session => session.Reply("hi there");

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(["hello"], Messages(MessageRole.User));
        Assert.Equal(["hi there"], Messages(MessageRole.Assistant));
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
        await Until(() => !service.IsBusy(workspace.Id));

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
        await Until(() => !service.IsBusy(workspace.Id));

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
    public async Task Prompt_SentWhileTheAiWorksIsNotFollowedUpWhenTheConversationShowsItAsInputTheAiGot()
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

    [Theory]
    [InlineData(WorkspaceState.Running)]
    public async Task Prompt_IsRefusedWhileOtherWorkHoldsTheWorkspace(WorkspaceState busy)
    {
        activity.TryEnter(workspace.Id, busy);

        await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Empty(prompter.Sessions);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(busy, activity.Get(workspace.Id));
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

    private PromptModel Model(string id, params PromptModelSetting[] settings) => new() { Id = id, Name = id, CallsTools = true, Settings = settings };

    private Mock<IPromptProvider> Forgetful()
    {
        Mock<IPromptProvider> forgetful = new();
        forgetful.Setup(x => x.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string? key, CancellationToken cancellation) => prompter.GetModels(key, cancellation));
        forgetful.Setup(x => x.GetModel(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string id, string? key, CancellationToken cancellation) => prompter.GetModel(id, key, cancellation));
        forgetful.Setup(x => x.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).Returns((PromptSessionOptions? options, CancellationToken cancellation) => prompter.Create(options, cancellation));
        forgetful.Setup(x => x.Load(It.IsAny<string>(), It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).Returns(async (string id, PromptSessionOptions? options, CancellationToken cancellation) =>
        {
            IPromptSession loaded = await prompter.Load(id, options, cancellation);
            loaded.Configuration.Model = null;
            return loaded;
        });
        service = new PromptService(workspaces.Object, store.Object, (_, _) => forgetful.Object, hub.Object, activity, snapshots.Object);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);
        return forgetful;
    }

    [Fact]
    public async Task Prompt_OpensASessionOnTheProviderOfTheServiceInTheWorkspaceFolderConfiguresItAndAttachesItsIdentifier()
    {
        prompter.OnCreate = session => session.Reply("ok");
        codex.OnCreate = session => session.Reply("ok");
        codex.Models = [Model("gpt-5.6-terra", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high", "max"]))];
        Use(workspace with { Service = AiService.Codex });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        IPromptSessionConfiguration configuration = codex.Sessions.Single().Configuration;
        Assert.Empty(prompter.Sessions);
        Assert.Equal("gpt-5.6-terra", configuration.Model?.Id);
        Assert.Equal("medium", configuration.ModelSettings["effort"]);
        Assert.True(configuration.IsPersistent);
        Assert.True(configuration.EnableShell);
        Assert.Contains("in the foreground", configuration.Instructions);
        Assert.Contains("status update", configuration.Instructions);
        Assert.IsAssignableFrom<IDiskStore>(configuration.Workspace);
        Assert.False(((IDiskStore)configuration.Workspace!).IsSandboxed);
        workspaces.Verify(x => x.AttachAiSession(workspace.Id, codex.Sessions.Single().Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prompt_StartsANewSessionOnTheDefaultModelOfTheServiceWithItsDefaultSettingsOrOtherwiseTheFirstModel()
    {
        prompter.OnCreate = session => session.Reply("ok");
        codex.OnCreate = session => session.Reply("ok");
        prompter.Models = [Model("opus"), Model("sonnet", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]))];
        codex.Models = [Model("first", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"])), Model("second")];

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        Use(workspace with { Service = AiService.Codex, AiSessionId = null });
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(("sonnet", "high"), (prompter.Sessions.Single().Configuration.Model?.Id, prompter.Sessions.Single().Configuration.ModelSettings["effort"]));
        Assert.True(prompter.Sessions.Single().IsDisposed);
        Assert.Equal(("first", "medium"), (codex.Sessions.Single().Configuration.Model?.Id, codex.Sessions.Single().Configuration.ModelSettings["effort"]));
    }

    [Fact]
    public async Task Prompt_RefusesAServiceThatOffersNoModelsAndLeavesTheWorkspaceReady()
    {
        codex.Models = [];
        Use(workspace with { Service = AiService.OpenCode });
        service = new PromptService(workspaces.Object, store.Object, (_, _) => codex, hub.Object, activity, snapshots.Object);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Contains("offers no models", exception.Message);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        Assert.Empty(codex.Sessions);
    }

    [Fact]
    public async Task GetAi_ReadsTheModelAndSettingsOfTheSessionAndMakesTheSessionWhenThereIsNone()
    {
        prompter.Models = [Model("sonnet", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]), PromptModelSetting.Switch("thinking", "Thinking"))];

        WorkspaceAi ai = await service.GetAi(workspace.Id, CancellationToken.None);

        Assert.Equal("sonnet", ai.Model);
        Assert.Equal(new Dictionary<string, string> { ["effort"] = "high", ["thinking"] = "false" }, ai.ModelSettings.ToDictionary(pair => pair.Key, pair => pair.Value));
        Assert.Equal(Session.Id, workspace.AiSessionId);
        workspaces.Verify(x => x.AttachAiSession(workspace.Id, Session.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("high", "high")]
    [InlineData("ultra", "medium")]
    [InlineData(null, "medium")]
    public async Task SetAi_GivesTheSessionTheModelWithTheValuesItTakesAndTheDefaultForTheRest(string? effort, string expected)
    {
        prompter.Models = [Model("small"), Model("big", PromptModelSetting.Choose("effort", "Effort", ["low", "medium", "high"]), PromptModelSetting.Switch("thinking", "Thinking", true), PromptModelSetting.Count("context", "Context", 1024, 32768, 4096))];
        Dictionary<string, string> chosen = new() { ["thinking"] = "false", ["context"] = "16384", ["gone"] = "x" };

        if (effort is not null)
        {
            chosen["effort"] = effort;
        }

        await service.SetAi(workspace.Id, "big", chosen, CancellationToken.None);

        Assert.Equal("big", Session.Configuration.Model?.Id);
        Assert.Equal(new Dictionary<string, string> { ["effort"] = expected, ["thinking"] = "false", ["context"] = "16384" }, Session.Configuration.ModelSettings.ToDictionary(pair => pair.Key, pair => pair.Value));
        WorkspaceAi ai = await service.GetAi(workspace.Id, CancellationToken.None);
        Assert.Equal(("big", "16384"), (ai.Model, ai.ModelSettings["context"]));
    }

    [Fact]
    public async Task SetAi_RefusesAModelTheServiceDoesNotOfferAndKeepsTheSessionsModel()
    {
        prompter.Models = [Model("sonnet")];
        await service.GetAi(workspace.Id, CancellationToken.None);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.SetAi(workspace.Id, "retired", new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("retired", exception.Message);
        Assert.Equal("sonnet", Session.Configuration.Model?.Id);
    }

    [Fact]
    public async Task SetAi_KeepsAChoiceTheSessionHasNotStoredYetWhenTheIdleSessionIsClosedAndOpenedAgain()
    {
        prompter.Models = [Model("sonnet"), Model("big", PromptModelSetting.Count("context", "Context", 1024, 32768, 4096))];
        Forgetful();
        await service.SetAi(workspace.Id, "big", new Dictionary<string, string> { ["context"] = "16384" }, CancellationToken.None);

        await service.Release(workspace.Id);
        WorkspaceAi ai = await service.GetAi(workspace.Id, CancellationToken.None);
        WorkspaceAi again = await service.GetAi(workspace.Id, CancellationToken.None);

        Assert.Equal(("big", "16384"), (ai.Model, ai.ModelSettings["context"]));
        Assert.Equal(ai.Model, again.Model);
        Assert.Single(prompter.Sessions);
    }

    [Fact]
    public async Task GetAi_ShowsNoModelForASessionWhoseStoredModelIsNoLongerOfferedAndAPromptAsksForOne()
    {
        prompter.OnCreate = session => session.Reply("ok");
        IPromptSession existing = await prompter.Create(new PromptSessionOptions { WorkspacePath = workspace.Folder, Sandbox = false });
        await existing.DisposeAsync();
        Use(workspace with { AiSessionId = existing.Id });
        Forgetful();

        WorkspaceAi ai = await service.GetAi(workspace.Id, CancellationToken.None);
        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Null(ai.Model);
        Assert.Contains("Choose one in the workspace settings", exception.Message);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));

        await service.SetAi(workspace.Id, "fake-model", new Dictionary<string, string>(), CancellationToken.None);
        await service.Prompt(workspace.Id, "hello again", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        Assert.Equal(["hello again"], Session.Prompts.Select(prompt => prompt.Text).ToList());
    }

    [Fact]
    public async Task ChangingTheService_ClosesTheSessionOfTheOldOneAndOpensASessionOfTheNewOneWithoutTheOldChoice()
    {
        prompter.Models = [Model("sonnet"), Model("opus")];
        codex.Models = [Model("gpt-5.6-terra")];
        await service.SetAi(workspace.Id, "opus", new Dictionary<string, string>(), CancellationToken.None);
        FakePromptSession old = Session;

        Use(workspace with { Service = AiService.Codex, AiSessionId = null });
        WorkspaceAi ai = await service.GetAi(workspace.Id, CancellationToken.None);

        Assert.True(old.IsDisposed);
        Assert.Equal("gpt-5.6-terra", ai.Model);
        Assert.Single(codex.Sessions);
    }

    [Fact]
    public async Task Prompt_ResumesTheAttachedLiveSessionAndKeepsItForTheNextPrompt()
    {
        IPromptSession existing = await prompter.Create(new PromptSessionOptions { WorkspacePath = workspace.Folder, Sandbox = false });
        ((FakePromptSession)existing).ReplyWith(_ => [new PromptMessageEvent { Text = "again" }]);
        await existing.DisposeAsync();
        Use(workspace with { AiSessionId = existing.Id });

        await service.Prompt(workspace.Id, "one", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "two", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(["one", "two"], Session.Prompts.Select(prompt => prompt.Text).ToList());
        workspaces.Verify(x => x.AttachAiSession(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prompt_MovesAnAiSessionStoredByAnOlderServerIntoALiveSession()
    {
        string legacy = Guid.NewGuid().ToString();
        prompter.OnCreate = session => session.Reply("ok");
        Use(workspace with { AiSessionId = legacy });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Equal(legacy, Session.AttachedTo);
        Assert.NotEqual(legacy, workspace.AiSessionId);
        Assert.Equal(Session.Id, workspace.AiSessionId);
    }

    [Fact]
    public async Task Prompt_StartsANewSessionWhenTheStoredOneIsGone()
    {
        prompter.OnCreate = session => session.Reply("ok");
        Use(workspace with { AiSessionId = Guid.NewGuid().ToString("N") });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Single(prompter.Sessions);
        Assert.Equal(Session.Id, workspace.AiSessionId);
    }

    [Fact]
    public async Task Prompt_StartsANewSessionInTheCurrentFolderWhenTheStoredOneRanInAFolderThatWasRenamed()
    {
        prompter.OnCreate = session => session.Reply("ok");
        Mock<IPromptProvider> renamed = new();
        renamed.Setup(x => x.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string? key, CancellationToken cancellation) => prompter.GetModels(key, cancellation));
        renamed.Setup(x => x.GetModel(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string id, string? key, CancellationToken cancellation) => prompter.GetModel(id, key, cancellation));
        renamed.Setup(x => x.Load(It.IsAny<string>(), It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DirectoryNotFoundException());
        renamed.Setup(x => x.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).Returns((PromptSessionOptions? options, CancellationToken cancellation) => prompter.Create(options, cancellation));
        service = new PromptService(workspaces.Object, store.Object, (_, _) => renamed.Object, hub.Object, activity, snapshots.Object);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);
        Use(workspace with { AiSessionId = Guid.NewGuid().ToString("N") });

        await service.Prompt(workspace.Id, "hello", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        Assert.Empty(Events(RemoteEventKind.Failed));
        Assert.Equal(Session.Id, workspace.AiSessionId);
        Assert.Equal(["hello"], Session.Prompts.Select(prompt => prompt.Text).ToList());
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
    public async Task Prompt_ClosesTheSessionItMadeWhenItCannotBeAttachedToTheWorkspace()
    {
        workspaces.Setup(x => x.AttachAiSession(workspace.Id, It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

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
        Mock<IPromptProvider> broken = new();
        broken.Setup(x => x.GetModels(It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string? key, CancellationToken cancellation) => prompter.GetModels(key, cancellation));
        broken.Setup(x => x.GetModel(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns((string id, string? key, CancellationToken cancellation) => prompter.GetModel(id, key, cancellation));
        broken.Setup(x => x.Create(It.IsAny<PromptSessionOptions?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new DirectoryNotFoundException("The workspace folder does not exist."));
        service = new PromptService(workspaces.Object, store.Object, (_, _) => broken.Object, hub.Object, activity, snapshots.Object);
        service.Configure(new ServerConfig { Port = 0, Key = "k" }, stopping.Token);

        RequestException exception = await Assert.ThrowsAsync<RequestException>(() => service.Prompt(workspace.Id, "hello", CancellationToken.None));

        Assert.Contains("does not exist", exception.Message);
        Assert.False(service.IsBusy(workspace.Id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(workspace.Id));
        snapshots.Verify(x => x.Discard(workspace.Id), Times.Once);
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

    [Fact]
    public async Task Interrupt_WithAShellCommandStillRunningClosesTheSessionSoTheCommandEndsAndTheNextPromptReopensIt()
    {
        prompter.OnCreate = session => session.Hang().Reply("after");
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => prompter.Sessions.Count == 1 && Session.Prompts.Count == 1);
        FakePromptSession first = Session;
        await first.Configuration.BeforeToolCall!(Command(), CancellationToken.None);

        await service.Interrupt(workspace.Id);

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
        prompter.OnCreate = session => session.Hang();
        await service.Prompt(workspace.Id, "long job", CancellationToken.None);
        await Until(() => prompter.Sessions.Count == 1 && Session.Prompts.Count == 1);
        await Session.Configuration.BeforeToolCall!(Command(), CancellationToken.None);
        await Session.Configuration.AfterToolCall!(Command(), new PromptToolResultEvent { Id = "1", Output = "done" }, TimeSpan.FromSeconds(1), CancellationToken.None);

        await service.Interrupt(workspace.Id);

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

        ChatHistory newest = await service.History(workspace.Id, null, 4, CancellationToken.None);
        Assert.Equal(["q2", "reply to q2", "q3", "reply to q3"], newest.Messages.Select(message => message.Text).ToList());
        Assert.NotNull(newest.Older);

        ChatHistory earlier = await service.History(workspace.Id, newest.Older, 4, CancellationToken.None);
        Assert.Equal(["q1", "reply to q1"], earlier.Messages.Select(message => message.Text).ToList());
        Assert.Null(earlier.Older);
        Assert.Equal([MessageRole.User, MessageRole.Assistant], earlier.Messages.Select(message => message.Role).ToList());
    }

    [Fact]
    public async Task History_ReportsWhetherTheAiWorksAndSkipsToolsInterruptNotesAndRemindersOfTheHost()
    {
        prompter.OnCreate = session => session.ReplyWith(prompt => prompt.Text.StartsWith("tools", StringComparison.Ordinal)
            ? [new PromptToolCallEvent { Id = "1", Name = "shell", Input = "{}" }, new PromptToolResultEvent { Id = "1", Output = "done" }, new PromptMessageEvent { Text = "used tools" }]
            : [new PromptMessageEvent { Text = "ok" }]);
        await service.Prompt(workspace.Id, "tools please", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "[Request interrupted by user]", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));
        await service.Prompt(workspace.Id, "<system-reminder>\nNote from the host application: x\n</system-reminder>", CancellationToken.None);
        await Until(() => !service.IsBusy(workspace.Id));

        ChatHistory history = await service.History(workspace.Id, null, null, CancellationToken.None);

        Assert.Equal(["tools please", "used tools", "ok", "ok"], history.Messages.Select(message => message.Text).ToList());
        Assert.False(history.Working);
        Assert.Null(history.Older);
    }

    [Fact]
    public async Task History_WithoutAConversationIsEmptyAndANonsenseCursorIsRefused()
    {
        ChatHistory history = await service.History(workspace.Id, null, null, CancellationToken.None);

        Assert.Empty(history.Messages);
        Assert.Null(history.Older);
        Assert.Empty(prompter.Sessions);
        await Assert.ThrowsAsync<RequestException>(() => service.History(workspace.Id, "nonsense", null, CancellationToken.None));
    }

    [Fact]
    public async Task History_OfAWorkspaceFromAnOlderServerOpensItsConversation()
    {
        string legacy = Guid.NewGuid().ToString();
        Use(workspace with { AiSessionId = legacy });

        ChatHistory history = await service.History(workspace.Id, null, null, CancellationToken.None);

        Assert.Empty(history.Messages);
        Assert.Equal(legacy, Session.AttachedTo);
        Assert.Equal(Session.Id, workspace.AiSessionId);
    }

    private IEnumerable<PromptEvent> Failing(Task gate)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptFailureEvent { Message = "boom" }];
    }

    private IEnumerable<PromptEvent> Slow(Task gate, string text)
    {
        gate.GetAwaiter().GetResult();
        return [new PromptMessageEvent { Text = text }];
    }
}
