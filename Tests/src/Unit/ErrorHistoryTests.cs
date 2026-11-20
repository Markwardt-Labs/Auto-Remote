namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ErrorHistoryTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan span) => now += span;
    }

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly EventHub hub = new();

    private readonly ManualTime time = new();

    private readonly Dictionary<Guid, IReadOnlyList<ErrorEntry>> stored = [];

    private readonly List<RemoteEvent> changed = [];

    private readonly WorkspaceInfo first = new() { Id = Guid.NewGuid(), Folder = "/work/first" };

    private readonly WorkspaceInfo second = new() { Id = Guid.NewGuid(), Folder = "/work/second" };

    private readonly ErrorHistory history;

    public ErrorHistoryTests()
    {
        store.Setup(x => x.LoadErrors(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => stored.GetValueOrDefault(id) ?? []);
        store.Setup(x => x.SaveErrors(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ErrorEntry>>(), It.IsAny<CancellationToken>())).Returns((Guid id, IReadOnlyList<ErrorEntry> errors, CancellationToken _) =>
        {
            lock (stored)
            {
                stored[id] = errors;
            }

            return Task.CompletedTask;
        });
        workspaces.Setup(x => x.Get(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((Guid id, CancellationToken _) => id == first.Id ? first : id == second.Id ? second : throw new RequestException("No such workspace."));
        workspaces.Setup(x => x.List(It.IsAny<CancellationToken>())).ReturnsAsync(new WorkspaceCatalog { Groups = [], Workspaces = [first, second] });
        hub.Subscribe(remoteEvent =>
        {
            if (remoteEvent.Kind == RemoteEventKind.ErrorsChanged)
            {
                lock (changed)
                {
                    changed.Add(remoteEvent);
                }
            }
        });
        history = new ErrorHistory(store.Object, workspaces.Object, hub, time);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 250 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }

    private void Fail(Guid id, string message) => hub.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = id, Error = message });

    private int Changes()
    {
        lock (changed)
        {
            return changed.Count;
        }
    }

    [Fact]
    public async Task Open_RecordsEveryFailureOfAWorkspaceWithItsTimeAndTellsEveryClient()
    {
        await history.Open(CancellationToken.None);

        Fail(first.Id, "The AI could not be started");
        await Until(() => Changes() == 1);
        time.Advance(TimeSpan.FromMinutes(5));
        Fail(first.Id, "Connection refused");
        await Until(() => Changes() == 2);

        IReadOnlyList<ErrorEntry> listed = await history.List(first.Id, CancellationToken.None);

        Assert.Equal(["Connection refused", "The AI could not be started"], listed.Select(entry => entry.Message));
        Assert.Equal([new DateTimeOffset(2026, 11, 20, 12, 5, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)], listed.Select(entry => entry.Time));
        RemoteEvent last = changed[^1];
        Assert.Equal(first.Id, last.WorkspaceId);
        Assert.Equal(listed, last.Errors);
    }

    [Fact]
    public async Task AnErrorOfOneWorkspaceIsNeverListedOrAnnouncedInAnother()
    {
        await history.Open(CancellationToken.None);

        Fail(first.Id, "only in the first");
        await Until(() => Changes() == 1);
        Fail(second.Id, "only in the second");
        await Until(() => Changes() == 2);

        Assert.Equal(["only in the first"], (await history.List(first.Id, CancellationToken.None)).Select(entry => entry.Message));
        Assert.Equal(["only in the second"], (await history.List(second.Id, CancellationToken.None)).Select(entry => entry.Message));
        Assert.Equal([first.Id, second.Id], changed.Select(remoteEvent => remoteEvent.WorkspaceId));
        Assert.Equal(["only in the first"], changed[0].Errors!.Select(entry => entry.Message));
        Assert.Equal(["only in the second"], changed[1].Errors!.Select(entry => entry.Message));
        Assert.Equal(["only in the first"], stored[first.Id].Select(entry => entry.Message));
        Assert.Equal(["only in the second"], stored[second.Id].Select(entry => entry.Message));
    }

    [Fact]
    public async Task Open_IgnoresFailuresWithoutAMessageAndOfWorkspacesThatAreGone()
    {
        await history.Open(CancellationToken.None);

        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = first.Id });
        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.Failed, WorkspaceId = first.Id, Error = string.Empty });
        Fail(Guid.NewGuid(), "a workspace that was deleted");
        hub.Publish(new RemoteEvent { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = first.Id, Working = false });
        Fail(first.Id, "the one that counts");
        await Until(() => Changes() == 1);

        Assert.Equal(["the one that counts"], (await history.List(first.Id, CancellationToken.None)).Select(entry => entry.Message));
        Assert.Single(stored);
    }

    [Fact]
    public async Task List_RemovesTheErrorsOlderThanThirtyDaysAndKeepsTheOthers()
    {
        await history.Open(CancellationToken.None);
        Fail(first.Id, "old");
        await Until(() => Changes() == 1);
        time.Advance(TimeSpan.FromDays(29));
        Fail(first.Id, "recent");
        await Until(() => Changes() == 2);

        Assert.Equal(["recent", "old"], (await history.List(first.Id, CancellationToken.None)).Select(entry => entry.Message));

        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(["recent"], (await history.List(first.Id, CancellationToken.None)).Select(entry => entry.Message));
        Assert.Equal(["recent"], stored[first.Id].Select(entry => entry.Message));

        time.Advance(TimeSpan.FromDays(30));

        Assert.Empty(await history.List(first.Id, CancellationToken.None));
        Assert.Empty(stored[first.Id]);
    }

    [Fact]
    public async Task Open_RemovesTheErrorsThatAreTooOldFromEveryWorkspaceAtOnce()
    {
        DateTimeOffset now = time.GetUtcNow();
        stored[first.Id] = [new ErrorEntry { Time = now - TimeSpan.FromDays(40), Message = "ancient" }, new ErrorEntry { Time = now - TimeSpan.FromDays(2), Message = "fresh" }];
        stored[second.Id] = [new ErrorEntry { Time = now - TimeSpan.FromDays(31), Message = "stale" }];

        await history.Open(CancellationToken.None);

        Assert.Equal(["fresh"], stored[first.Id].Select(entry => entry.Message));
        Assert.Empty(stored[second.Id]);
    }

    [Fact]
    public async Task Open_GoesOnRemovingOldErrorsWhileTheServerRuns()
    {
        using CancellationTokenSource stopping = new();
        ErrorHistory sweeping = new(store.Object, workspaces.Object, hub, TimeProvider.System) { SweepEvery = TimeSpan.FromMilliseconds(40) };
        await sweeping.Open(stopping.Token);

        stored[first.Id] = [new ErrorEntry { Time = DateTimeOffset.UtcNow - TimeSpan.FromDays(45), Message = "ancient" }];
        await Until(() => stored[first.Id].Count == 0);

        await stopping.CancelAsync();
    }

    [Fact]
    public async Task Record_KeepsOnlyTheNewestEntriesBeyondTheLimit()
    {
        ErrorHistory limited = new(store.Object, workspaces.Object, hub, time) { MaxEntries = 3 };
        await limited.Open(CancellationToken.None);

        for (int index = 1; index <= 5; index++)
        {
            Fail(first.Id, $"error {index}");
            await Until(() => Changes() == index);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(["error 5", "error 4", "error 3"], (await limited.List(first.Id, CancellationToken.None)).Select(entry => entry.Message));
    }

    [Fact]
    public async Task List_RefusesAWorkspaceThatDoesNotExist()
    {
        await Assert.ThrowsAsync<RequestException>(() => history.List(Guid.NewGuid(), CancellationToken.None));
    }
}
