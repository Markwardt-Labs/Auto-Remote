namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SavedInputsTests
{
    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IEventHub> events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly Guid id = Guid.NewGuid();

    private List<SavedInput> stored = [];

    private readonly Mock<ISpeechService> speech = new();

    private readonly SavedInputs inputs;

    public SavedInputsTests()
    {
        store.Setup(x => x.LoadInputs(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => [.. stored]);
        store.Setup(x => x.SaveInputs(id, It.IsAny<IReadOnlyList<SavedInput>>(), It.IsAny<CancellationToken>())).Returns((Guid _, IReadOnlyList<SavedInput> saved, CancellationToken _) =>
        {
            stored = [.. saved];
            return Task.CompletedTask;
        });
        workspaces.Setup(x => x.Get(id, It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceInfo { Id = id, Folder = "/work/app" });
        events.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(published.Add);
        inputs = new SavedInputs(store.Object, workspaces.Object, events.Object, speech.Object);
    }

    [Fact]
    public async Task Save_AddsTheNewestEntryFirstAndTellsEveryClient()
    {
        await inputs.Save(id, "first", true, CancellationToken.None);
        await Task.Delay(5);

        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "second", true, CancellationToken.None);

        Assert.Equal(["second", "first"], saved.Select(input => input.Text).ToList());
        Assert.Equal(["second", "first"], (await inputs.List(id, CancellationToken.None)).Select(input => input.Text).ToList());
        Assert.Equal(2, published.Count);
        Assert.All(published, remoteEvent => Assert.Equal((RemoteEventKind.SavedInputsChanged, id), (remoteEvent.Kind, remoteEvent.WorkspaceId)));
        Assert.Equal(["second", "first"], published[^1].SavedInputs!.Select(input => input.Text).ToList());
    }

    [Fact]
    public async Task List_SortsByTheTimeSavedWhateverTheStoredOrder()
    {
        stored = [new SavedInput { Id = Guid.NewGuid(), Text = "old", Time = DateTimeOffset.UnixEpoch }, new SavedInput { Id = Guid.NewGuid(), Text = "new", Time = DateTimeOffset.UnixEpoch.AddHours(1) }, new SavedInput { Id = Guid.NewGuid(), Text = "middle", Time = DateTimeOffset.UnixEpoch.AddMinutes(30) }];

        Assert.Equal(["new", "middle", "old"], (await inputs.List(id, CancellationToken.None)).Select(input => input.Text).ToList());
    }

    [Fact]
    public async Task Queue_PutsQueuedInputsFirstInTheOrderTheyAreSentAndResumesThemAll()
    {
        await inputs.Save(id, "saved", true, CancellationToken.None);
        await inputs.Queue(id, "first", true, CancellationToken.None);
        await inputs.SetPaused(id, true, CancellationToken.None);
        await Task.Delay(5);
        await inputs.Save(id, "saved later", true, CancellationToken.None);

        IReadOnlyList<SavedInput> queued = await inputs.Queue(id, "second", true, CancellationToken.None);

        Assert.Equal(["first", "second", "saved later", "saved"], queued.Select(input => input.Text).ToList());
        Assert.Equal([true, true, false, false], queued.Select(input => input.IsQueued).ToList());
        Assert.All(queued, input => Assert.False(input.IsPaused));
        Assert.True(inputs.HasQueued(id));
        Assert.Equal([id], inputs.Waiting);
    }

    [Fact]
    public async Task SetPaused_TellsOnlyOfAChangeAndAPausedQueueIsNotWaitingToBeSent()
    {
        await inputs.Queue(id, "first", true, CancellationToken.None);
        published.Clear();

        await inputs.SetPaused(id, true, CancellationToken.None);
        await inputs.SetPaused(id, true, CancellationToken.None);

        Assert.True(Assert.Single(Assert.Single(published).SavedInputs!).IsPaused);
        Assert.True(inputs.HasQueued(id));
        Assert.Empty(inputs.Waiting);
        Assert.False(await inputs.SendNext(id, _ => throw new InvalidOperationException("Sent a paused input."), CancellationToken.None));
    }

    [Fact]
    public async Task SendNext_SendsTheOldestQueuedInputAndRemovesItOnlyOnceItWasSent()
    {
        await inputs.Save(id, "saved", true, CancellationToken.None);
        await inputs.Queue(id, "first", true, CancellationToken.None);
        await Task.Delay(5);
        await inputs.Queue(id, "second", true, CancellationToken.None);
        await Assert.ThrowsAsync<RequestException>(() => inputs.SendNext(id, _ => throw new RequestException("busy"), CancellationToken.None));
        List<string> sent = [];

        while (await inputs.SendNext(id, input =>
        {
            sent.Add(input.Text);
            return Task.CompletedTask;
        }, CancellationToken.None))
        {
        }

        Assert.Equal(["first", "second"], sent);
        Assert.Equal(["saved"], stored.Select(input => input.Text).ToList());
        Assert.False(inputs.HasQueued(id));
        Assert.Empty(inputs.Waiting);
    }

    [Fact]
    public async Task SetAutoCommit_ChangesOneEntryWhichKeepsItWhenTakenAndTellsEveryClient()
    {
        SavedInput first = Assert.Single(await inputs.Save(id, "first", true, CancellationToken.None));
        await Task.Delay(5);
        await inputs.Queue(id, "second", false, CancellationToken.None);
        published.Clear();

        IReadOnlyList<SavedInput> changed = await inputs.SetAutoCommit(id, first.Id, false, CancellationToken.None);

        Assert.Equal([("second", false), ("first", false)], changed.Select(input => (input.Text, input.AutoCommit)).ToList());
        Assert.Single(published);
        Assert.False((await inputs.Take(id, first.Id, CancellationToken.None)).AutoCommit);
        await Assert.ThrowsAsync<RequestException>(() => inputs.SetAutoCommit(id, first.Id, true, CancellationToken.None));
        Assert.True(inputs.HasQueued(id));
    }

    [Fact]
    public async Task Keep_AndEntriesStoredBeforeTheyHadOneHaveAutoCommitOn()
    {
        Assert.True(Assert.Single(await inputs.Keep(id, "kept", CancellationToken.None)).AutoCommit);

        SavedInput old = JsonSerializer.Deserialize<SavedInput>("{\"id\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"text\":\"old\",\"time\":\"2026-01-01T00:00:00Z\"}", new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;

        Assert.True(old.AutoCommit);
        Assert.False(JsonSerializer.Deserialize<SavedInput>(JsonSerializer.Serialize(old with { AutoCommit = false }))!.AutoCommit);
    }

    [Fact]
    public async Task Take_GivesAQueuedInputBackAsAnOrdinaryOneAndOutOfTheQueue()
    {
        SavedInput queued = Assert.Single(await inputs.Queue(id, "first", true, CancellationToken.None));

        SavedInput taken = await inputs.Take(id, queued.Id, CancellationToken.None);

        Assert.Equal(("first", false, false), (taken.Text, taken.IsQueued, taken.IsPaused));
        Assert.False(inputs.HasQueued(id));
        Assert.Empty(stored);
    }

    [Fact]
    public async Task Save_DropsTheOldestSavedInputsBeforeAnyQueuedOne()
    {
        stored = [new SavedInput { Id = Guid.NewGuid(), Text = "queued long ago", Time = DateTimeOffset.UnixEpoch, IsQueued = true }, .. Enumerable.Range(1, 199).Select(index => new SavedInput { Id = Guid.NewGuid(), Text = $"saved {index}", Time = DateTimeOffset.UnixEpoch.AddMinutes(index) })];

        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "newest", true, CancellationToken.None);

        Assert.Equal(200, saved.Count);
        Assert.Equal(["queued long ago", "newest"], saved.Take(2).Select(input => input.Text).ToList());
        Assert.DoesNotContain(saved, input => input.Text == "saved 1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task Queue_RefusesAnEmptyInput(string text) => await Assert.ThrowsAsync<RequestException>(() => inputs.Queue(id, text, true, CancellationToken.None));

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task Save_RefusesAnEmptyInput(string text)
    {
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(id, text, true, CancellationToken.None));

        Assert.Empty(stored);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Save_RefusesATooLongInputAndKeepsTheTextExactly()
    {
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(id, new string('x', 100001), true, CancellationToken.None));

        await inputs.Save(id, "  indented\n\ttext  ", true, CancellationToken.None);

        Assert.Equal("  indented\n\ttext  ", Assert.Single(stored).Text);
    }

    [Fact]
    public async Task Keep_SavesAPromptThatWillNotRunHoweverLongItIs()
    {
        string text = new('a', 150_000);

        IReadOnlyList<SavedInput> saved = await inputs.Keep(id, text, CancellationToken.None);

        Assert.Equal(text, Assert.Single(saved).Text);
    }

    [Fact]
    public async Task Save_DropsTheOldestEntriesBeyondTheLimit()
    {
        for (int number = 1; number <= 202; number++)
        {
            await inputs.Save(id, $"entry {number}", true, CancellationToken.None);
        }

        Assert.Equal(200, stored.Count);
        Assert.DoesNotContain(stored, input => input.Text is "entry 1" or "entry 2");
        Assert.Equal("entry 202", (await inputs.List(id, CancellationToken.None))[0].Text);
    }

    [Fact]
    public async Task Take_RemovesTheEntryTellsEveryClientAndGivesItToOnlyOneCaller()
    {
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "later", true, CancellationToken.None);
        published.Clear();

        SavedInput taken = await inputs.Take(id, saved[0].Id, CancellationToken.None);

        Assert.Equal("later", taken.Text);
        Assert.Empty(stored);
        Assert.Empty(Assert.Single(published).SavedInputs!);
        await Assert.ThrowsAsync<RequestException>(() => inputs.Take(id, saved[0].Id, CancellationToken.None));
    }

    [Fact]
    public async Task Take_LeavesTheOtherEntries()
    {
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "keep", true, CancellationToken.None);
        await Task.Delay(5);
        IReadOnlyList<SavedInput> both = await inputs.Save(id, "take", true, CancellationToken.None);

        await inputs.Take(id, both[0].Id, CancellationToken.None);

        Assert.Equal(["keep"], stored.Select(input => input.Text).ToList());
        Assert.Equal(saved[0].Id, stored[0].Id);
    }

    [Fact]
    public async Task EveryCall_RefusesAnUnknownWorkspace()
    {
        Guid other = Guid.NewGuid();
        workspaces.Setup(x => x.Get(other, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("No such workspace."));

        await Assert.ThrowsAsync<RequestException>(() => inputs.List(other, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(other, "x", true, CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => inputs.Take(other, Guid.NewGuid(), CancellationToken.None));
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 400 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public async Task Transcribe_AddsAnEntryBeingConvertedAtOnceAndFillsItWithTheTextAsAnOrdinarySavedInputWhenDone()
    {
        TaskCompletionSource<string> heard = new();
        speech.Setup(x => x.Transcribe("AQ==", It.IsAny<CancellationToken>())).Returns(heard.Task);

        IReadOnlyList<SavedInput> started = await inputs.Transcribe(id, "AQ==", CancellationToken.None);

        SavedInput entry = Assert.Single(started);
        Assert.Equal((true, string.Empty, null, false), (entry.IsConverting, entry.Text, entry.Error, entry.IsReady));
        Assert.Empty(stored);
        Assert.Equal(entry, Assert.Single(await inputs.List(id, CancellationToken.None)));
        Assert.Single(published);

        heard.SetResult("  hello there ");
        await Until(() => stored.Count == 1);

        SavedInput saved = Assert.Single(stored);
        Assert.Equal((entry.Id, "hello there", false, true, entry.Time), (saved.Id, saved.Text, saved.IsConverting, saved.IsReady, saved.Time));
        Assert.Equal("hello there", Assert.Single(published[^1].SavedInputs!).Text);
        SavedInput taken = await inputs.Take(id, entry.Id, CancellationToken.None);
        Assert.Equal("hello there", taken.Text);
    }

    [Fact]
    public async Task Transcribe_KeepsTheReasonOfAFailureInTheEntryUntilItIsDiscarded()
    {
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("Voice input is not set up on the server."));

        IReadOnlyList<SavedInput> started = await inputs.Transcribe(id, "AQ==", CancellationToken.None);
        await Until(() => published.Count == 2);

        SavedInput failed = Assert.Single(published[^1].SavedInputs!);
        Assert.Equal((started[0].Id, false, "Voice input is not set up on the server.", true), (failed.Id, failed.IsConverting, failed.Error, failed.HasError));
        Assert.Empty(stored);
        await Assert.ThrowsAsync<RequestException>(() => inputs.Take(id, failed.Id, CancellationToken.None));

        IReadOnlyList<SavedInput> left = await inputs.Discard(id, failed.Id, CancellationToken.None);

        Assert.Empty(left);
        Assert.Empty(await inputs.List(id, CancellationToken.None));
    }

    [Theory]
    [InlineData("   ", "Nothing was heard.")]
    public async Task Transcribe_MakesNothingHeardAFailureAndNeverSavesAnEmptyInput(string heard, string expected)
    {
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(heard);

        await inputs.Transcribe(id, "AQ==", CancellationToken.None);
        await Until(() => published.Count == 2);

        Assert.Equal(expected, Assert.Single(published[^1].SavedInputs!).Error);
        Assert.Empty(stored);
    }

    [Fact]
    public async Task Transcribe_ReportsAnEngineThatFailedUnexpectedlyWithoutTheDetails()
    {
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("secret path /x/y"));

        await inputs.Transcribe(id, "AQ==", CancellationToken.None);
        await Until(() => published.Count == 2);

        Assert.Equal("The speech could not be turned into text.", Assert.Single(published[^1].SavedInputs!).Error);
    }

    [Fact]
    public async Task Discard_CancelsAConversionThatIsRunningAndNeverSavesItsResult()
    {
        CancellationToken seen = default;
        TaskCompletionSource<string> heard = new();
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string _, CancellationToken cancellation) =>
        {
            seen = cancellation;
            return heard.Task;
        });
        IReadOnlyList<SavedInput> started = await inputs.Transcribe(id, "AQ==", CancellationToken.None);
        published.Clear();

        IReadOnlyList<SavedInput> left = await inputs.Discard(id, started[0].Id, CancellationToken.None);
        heard.SetResult("too late");
        await Task.Delay(100);

        Assert.True(seen.IsCancellationRequested);
        Assert.Empty(left);
        Assert.Empty(stored);
        Assert.Empty(Assert.Single(published).SavedInputs!);
    }

    [Fact]
    public async Task Discard_RemovesAStoredEntryAndIgnoresOneThatIsGone()
    {
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "drop", true, CancellationToken.None);
        published.Clear();

        Assert.Empty(await inputs.Discard(id, saved[0].Id, CancellationToken.None));
        Assert.Empty(stored);
        Assert.Single(published);

        Assert.Empty(await inputs.Discard(id, saved[0].Id, CancellationToken.None));
        Assert.Single(published);
    }

    [Fact]
    public async Task Take_RefusesAnEntryThatIsStillBeingConverted()
    {
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<string>().Task);
        IReadOnlyList<SavedInput> started = await inputs.Transcribe(id, "AQ==", CancellationToken.None);

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => inputs.Take(id, started[0].Id, CancellationToken.None));

        Assert.Contains("still being turned into text", refused.Message);
    }

    [Fact]
    public async Task Transcribe_RefusesAnEmptyRecordingAndAnUnknownWorkspace()
    {
        Guid other = Guid.NewGuid();
        workspaces.Setup(x => x.Get(other, It.IsAny<CancellationToken>())).ThrowsAsync(new RequestException("No such workspace."));

        await Assert.ThrowsAsync<RequestException>(() => inputs.Transcribe(id, " ", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => inputs.Transcribe(other, "AQ==", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => inputs.Discard(other, Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(published);
    }

    [Fact]
    public async Task Save_KeepsAnEntryBeingConvertedInTheListItPublishes()
    {
        speech.Setup(x => x.Transcribe(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(new TaskCompletionSource<string>().Task);
        await inputs.Transcribe(id, "AQ==", CancellationToken.None);

        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "typed", true, CancellationToken.None);

        Assert.Equal([true, false], saved.Select(input => input.IsConverting).OrderByDescending(converting => converting));
        Assert.Equal(2, published[^1].SavedInputs!.Count);
    }

    [Fact]
    public async Task Queue_RefusesMoreThanTheLimitAndSavingBesideAFullQueueLosesNothing()
    {
        stored = [.. Enumerable.Range(1, 200).Select(index => new SavedInput { Id = Guid.NewGuid(), Text = $"queued {index}", Time = DateTimeOffset.UnixEpoch.AddMinutes(index), IsQueued = true })];

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => inputs.Queue(id, "one more", true, CancellationToken.None));
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "kept", true, CancellationToken.None);

        Assert.Equal("200 inputs are queued already.", refused.Message);
        Assert.Equal(201, saved.Count);
        Assert.Equal("kept", saved[^1].Text);
    }

    [Fact]
    public async Task SendNext_NeverSendsAnInputTwiceThoughItCouldNotBeTakenOffTheDisk()
    {
        await inputs.Queue(id, "once", true, CancellationToken.None);
        store.Setup(x => x.SaveInputs(id, It.IsAny<IReadOnlyList<SavedInput>>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk full"));

        Assert.True(await inputs.SendNext(id, _ => Task.CompletedTask, CancellationToken.None));

        Assert.False(await inputs.SendNext(id, _ => throw new InvalidOperationException("Sent it again."), CancellationToken.None));
        Assert.Empty(await inputs.List(id, CancellationToken.None));
        Assert.Empty(inputs.Waiting);
    }

    [Fact]
    public async Task Forget_StopsCountingTheQueueOfAWorkspace()
    {
        await inputs.Queue(id, "left", true, CancellationToken.None);

        inputs.Forget(id);

        Assert.Empty(inputs.Waiting);
        Assert.False(inputs.HasQueued(id));
    }
}
