namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class SavedInputsTests
{
    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly Mock<IEventHub> events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly Guid id = Guid.NewGuid();

    private List<SavedInput> stored = [];

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
        inputs = new SavedInputs(store.Object, workspaces.Object, events.Object);
    }

    [Fact]
    public async Task Save_AddsTheNewestEntryFirstAndTellsEveryClient()
    {
        await inputs.Save(id, "first", CancellationToken.None);
        await Task.Delay(5);

        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "second", CancellationToken.None);

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

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public async Task Save_RefusesAnEmptyInput(string text)
    {
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(id, text, CancellationToken.None));

        Assert.Empty(stored);
        Assert.Empty(published);
    }

    [Fact]
    public async Task Save_RefusesATooLongInputAndKeepsTheTextExactly()
    {
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(id, new string('x', 100001), CancellationToken.None));

        await inputs.Save(id, "  indented\n\ttext  ", CancellationToken.None);

        Assert.Equal("  indented\n\ttext  ", Assert.Single(stored).Text);
    }

    [Fact]
    public async Task Save_DropsTheOldestEntriesBeyondTheLimit()
    {
        for (int number = 1; number <= 202; number++)
        {
            await inputs.Save(id, $"entry {number}", CancellationToken.None);
        }

        Assert.Equal(200, stored.Count);
        Assert.DoesNotContain(stored, input => input.Text is "entry 1" or "entry 2");
        Assert.Equal("entry 202", (await inputs.List(id, CancellationToken.None))[0].Text);
    }

    [Fact]
    public async Task Take_RemovesTheEntryTellsEveryClientAndGivesItToOnlyOneCaller()
    {
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "later", CancellationToken.None);
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
        IReadOnlyList<SavedInput> saved = await inputs.Save(id, "keep", CancellationToken.None);
        await Task.Delay(5);
        IReadOnlyList<SavedInput> both = await inputs.Save(id, "take", CancellationToken.None);

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
        await Assert.ThrowsAsync<RequestException>(() => inputs.Save(other, "x", CancellationToken.None));
        await Assert.ThrowsAsync<RequestException>(() => inputs.Take(other, Guid.NewGuid(), CancellationToken.None));
    }
}
