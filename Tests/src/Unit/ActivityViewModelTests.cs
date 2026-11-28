namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ActivityViewModelTests
{
    public ActivityViewModelTests() => model = new(connection.Object, workspace);

    private readonly Mock<IServerConnection> connection = new();

    private readonly Guid workspace = Guid.NewGuid();

    private readonly ActivityViewModel model;

    private ActivityEntry Entry(string title, int minutes) => new() { Id = Guid.NewGuid(), Title = title, StartedAt = DateTimeOffset.UnixEpoch.AddMinutes(minutes), Source = ActivitySource.Send };

    private void Page(string? before, string? older, params ActivityEntry[] entries) => connection.Setup(x => x.GetActivity(workspace, before, It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ActivityPage { Entries = entries, Older = older });

    private void Told(ActivityEntry entry, Guid? id = null) => model.Receive(new RemoteEvent { Kind = RemoteEventKind.ActivityChanged, WorkspaceId = id ?? workspace, ActivityEntry = entry });

    private string[] Titles() => [.. model.Entries.Select(row => row.Title)];

    [Fact]
    public async Task Load_ShowsTheNewestAndLoadMoreAddsOlderOnesUntilThereAreNone()
    {
        Page(null, "cursor", Entry("c", 3), Entry("b", 2));
        Page("cursor", null, Entry("a", 1));

        await model.Load();
        Assert.Equal((true, false), (model.HasMore, model.IsEmpty));

        model.LoadMoreCommand.Execute(null);

        Assert.Equal(["c", "b", "a"], Titles());
        Assert.False(model.HasMore);
    }

    [Fact]
    public async Task ChangedEntries_ReplaceThemselvesStayExpandedAndNewOnesTakeTheirPlaceByTime()
    {
        ActivityEntry first = Entry("first", 2);
        Page(null, "cursor", first);
        await model.Load();
        model.ToggleCommand.Execute(model.Entries[0]);

        Told(first with { Title = "first, done", Outcome = ActivityOutcome.Done });
        Told(Entry("newer", 5));
        Told(Entry("older than all read", 1));
        Told(Entry("between", 3));
        Told(Entry("elsewhere", 9), Guid.NewGuid());

        Assert.Equal(["newer", "between", "first, done"], Titles());
        Assert.True(model.Entries[2].IsExpanded);
        Assert.Equal("Done", model.Entries[2].Outcome);
    }

    [Fact]
    public async Task WhatIsToldWhileTheNewestAreReadIsNotLost()
    {
        TaskCompletionSource<ActivityPage> read = new();
        connection.Setup(x => x.GetActivity(workspace, null, It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(read.Task);
        Task loading = model.Load();

        Told(Entry("told", 5));
        read.SetResult(new ActivityPage { Entries = [Entry("read", 1)] });
        await loading;

        Assert.Equal(["told", "read"], Titles());
    }

    [Fact]
    public async Task NothingRead_SaysSoAndAFailureShowsItsReason()
    {
        Page(null, null);
        await model.Load();
        Assert.True(model.IsEmpty);

        connection.Setup(x => x.GetActivity(workspace, null, It.IsAny<int>(), It.IsAny<CancellationToken>())).ThrowsAsync(new RemoteException("No."));
        await model.Load();

        Assert.Equal("No.", model.Error);
    }

    [Theory]
    [InlineData("add parser", true, null, "add parser (pushed)")]
    [InlineData("add parser", false, null, "add parser (not pushed)")]
    [InlineData("add parser", false, "rejected", "add parser (not pushed)\nPush failed: rejected")]
    [InlineData(null, null, "no identity", "Commit failed: no identity")]
    [InlineData(null, null, null, "")]
    public void Rows_SayWhatCameOfCommitting(string? commit, bool? pushed, string? error, string expected)
    {
        ActivityEntryViewModel row = new(Entry("t", 0) with { Commit = commit, Pushed = pushed, CommitError = error });

        Assert.Equal((expected, expected.Length > 0), (row.CommitText, row.HasCommit));
    }
}
