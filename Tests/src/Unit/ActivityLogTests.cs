namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ActivityLogTests : IDisposable
{
    public ActivityLogTests()
    {
        events.Setup(x => x.Publish(It.IsAny<RemoteEvent>())).Callback<RemoteEvent>(published.Add);
        log = new ActivityLog(events.Object, null, 5);
        log.Open(temp.Path);
    }

    private readonly TempFolder temp = new();

    private readonly Mock<IEventHub> events = new();

    private readonly List<RemoteEvent> published = [];

    private readonly Guid id = Guid.NewGuid();

    private readonly ActivityLog log;

    public void Dispose() => temp.Dispose();

    private ActivityEntry Entry(string title) => new() { Id = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Source = ActivitySource.Queue, Title = title };

    private async Task<ActivityPage> Read(IActivityLog from, string? before = null, int? limit = null) => await from.Read(id, before, limit, CancellationToken.None);

    [Fact]
    public async Task EveryChange_IsKeptInOrderToldToClientsAndStillThereAfterARestart()
    {
        ActivityEntry entry = Entry("build");
        log.Add(id, entry);
        log.Note(id, entry.Id, "Tried again.");
        log.Change(id, entry.Id, current => current with { Outcome = ActivityOutcome.Done, Commit = "Add login" });

        ActivityEntry read = Assert.Single((await Read(log)).Entries);

        Assert.Equal((ActivityOutcome.Done, "Add login"), (read.Outcome, read.Commit));
        Assert.EndsWith("Tried again.", Assert.Single(read.Notes));
        Assert.Equal(3, published.Count(item => item.Kind == RemoteEventKind.ActivityChanged && item.WorkspaceId == id));
        ActivityLog restarted = new(events.Object);
        restarted.Open(temp.Path);
        Assert.Equal("Add login", Assert.Single((await Read(restarted)).Entries).Commit);
    }

    [Fact]
    public async Task Read_PagesFromTheNewestAndOnlyTheNewestAreKept()
    {
        for (int number = 1; number <= 7; number++)
        {
            log.Add(id, Entry($"entry {number}"));
        }

        ActivityPage newest = await Read(log, null, 2);
        ActivityPage older = await Read(log, newest.Older, 10);

        Assert.Equal(["entry 7", "entry 6"], newest.Entries.Select(entry => entry.Title).ToList());
        Assert.Equal(["entry 5", "entry 4", "entry 3"], older.Entries.Select(entry => entry.Title).ToList());
        Assert.Null(older.Older);
    }

    [Fact]
    public async Task AChangeOfAnEntryThatIsGoneOrThatChangesNothingTellsNobody()
    {
        ActivityEntry entry = Entry("one");
        log.Add(id, entry);
        log.Change(id, Guid.NewGuid(), current => current with { Reason = "no" });
        log.Change(id, entry.Id, current => current);
        await log.Flush();

        Assert.Single(published);
    }

    [Fact]
    public async Task Delete_RemovesTheActivityOfAWorkspace()
    {
        log.Add(id, Entry("one"));

        log.Delete(id);

        Assert.Empty((await Read(log)).Entries);
        Assert.Empty(Directory.GetFiles(temp.Combine("activity")));
    }
}
