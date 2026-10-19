namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceControlTests
{
    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IScriptRunner> scripts = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly Mock<IWorkspaceStore> store = new();

    private readonly Mock<IWorkspaceManager> workspaces = new();

    private readonly EventHub hub = new();

    private readonly WorkspaceActivity activity;

    private readonly WorkspaceControl control;

    private readonly Guid id = Guid.NewGuid();

    public WorkspaceControlTests()
    {
        activity = new WorkspaceActivity(hub);
        control = new WorkspaceControl(activity, prompts.Object, scripts.Object, versioning.Object, snapshots.Object, store.Object, workspaces.Object, hub);
        workspaces.Setup(x => x.List(It.IsAny<CancellationToken>())).ReturnsAsync(() => new WorkspaceCatalog { Groups = [], Workspaces = [new WorkspaceInfo { Id = id, Folder = "/work/app" }] });
        prompts.Setup(x => x.Unanswered(id)).ReturnsAsync(["do it"]);
        snapshots.Setup(x => x.Has(id)).Returns(true);
        store.Setup(x => x.LoadPaused(It.IsAny<CancellationToken>())).ReturnsAsync([]);
    }

    [Fact]
    public async Task Stop_InterruptsTheAiAndKeepsWhatItDid()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        prompts.Setup(x => x.Interrupt(id)).Returns(() =>
        {
            Assert.Equal(WorkspaceState.Stopping, activity.Get(id));
            return Task.CompletedTask;
        });

        await control.Stop(id);

        prompts.Verify(x => x.Interrupt(id), Times.Once);
        snapshots.Verify(x => x.Revert(It.IsAny<Guid>()), Times.Never);
        snapshots.Verify(x => x.Discard(id), Times.Once);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Fact]
    public async Task Stop_StopsEveryRunningScript()
    {
        activity.TryEnter(id, WorkspaceState.Running);

        await control.Stop(id);

        scripts.Verify(x => x.StopAll(id), Times.Once);
        snapshots.Verify(x => x.Revert(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Fact]
    public async Task Stop_UndoesAGitActionAndTheAiWorkingForIt()
    {
        activity.TryEnter(id, WorkspaceState.Managing);

        await control.Stop(id);

        versioning.Verify(x => x.Cancel(id), Times.Once);
        prompts.Verify(x => x.Interrupt(id), Times.Once);
        snapshots.Verify(x => x.Revert(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Theory]
    [InlineData(WorkspaceState.Working)]
    [InlineData(WorkspaceState.Paused)]
    public async Task Cancel_StopsTheAiThenRevertsItWhileCanceling(WorkspaceState working)
    {
        activity.TryEnter(id, WorkspaceState.Working);

        if (working == WorkspaceState.Paused)
        {
            activity.Pause(id);
        }

        snapshots.Setup(x => x.Revert(id)).Returns(() =>
        {
            Assert.Equal(WorkspaceState.Canceling, activity.Get(id));
            return Task.CompletedTask;
        });

        await control.Cancel(id);

        prompts.Verify(x => x.Interrupt(id), Times.Once);
        snapshots.Verify(x => x.Revert(id), Times.Once);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Fact]
    public async Task Cancel_WithoutASnapshotIsRefusedAndTheWorkKeepsGoing()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        snapshots.Setup(x => x.Has(id)).Returns(false);

        await Assert.ThrowsAsync<RequestException>(() => control.Cancel(id));

        prompts.Verify(x => x.Interrupt(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(WorkspaceState.Working, activity.Get(id));
    }

    [Fact]
    public async Task Cancel_ReturnsToReadyEvenWhenTheRevertFails()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        snapshots.Setup(x => x.Revert(id)).ThrowsAsync(new RequestException("git failed"));

        RequestException failure = await Assert.ThrowsAsync<RequestException>(() => control.Cancel(id));

        Assert.Equal("git failed", failure.Message);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Fact]
    public async Task StopAndCancel_AreRefusedWhenNothingRunsOrTheWorkspaceIsAlreadyHalting()
    {
        RequestException idle = await Assert.ThrowsAsync<RequestException>(() => control.Stop(id));
        Assert.Contains("Nothing is running", idle.Message);

        activity.TryEnter(id, WorkspaceState.Working);
        activity.Halt(id, WorkspaceState.Stopping);

        await Assert.ThrowsAsync<RequestException>(() => control.Stop(id));
        await Assert.ThrowsAsync<RequestException>(() => control.Cancel(id));
        prompts.Verify(x => x.Interrupt(It.IsAny<Guid>()), Times.Never);
        Assert.Equal(WorkspaceState.Stopping, activity.Get(id));
    }

    [Theory]
    [InlineData(WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing)]
    public async Task Cancel_IsRefusedForScriptsAndGitActionsWhichOnlyStop(WorkspaceState held)
    {
        activity.TryEnter(id, held);

        RequestException refused = await Assert.ThrowsAsync<RequestException>(() => control.Cancel(id));

        Assert.Contains("Stop it instead", refused.Message);
        Assert.Equal(held, activity.Get(id));
    }

    [Fact]
    public async Task Pause_SuspendsTheAiAndResumeContinuesIt()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        prompts.Setup(x => x.Pause(id)).Returns(() =>
        {
            Assert.Equal(WorkspaceState.Paused, activity.Get(id));
            return Task.CompletedTask;
        });

        await control.Pause(id);
        Assert.Equal(WorkspaceState.Paused, activity.Get(id));

        await control.Resume(id);

        prompts.Verify(x => x.Resume(id), Times.Once);
        Assert.Equal(WorkspaceState.Working, activity.Get(id));
    }

    [Fact]
    public async Task Pause_GoesBackWhenTheAiCouldNotBePaused()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        prompts.Setup(x => x.IsBusy(id)).Returns(true);
        prompts.Setup(x => x.Pause(id)).ThrowsAsync(new RequestException("no"));

        await Assert.ThrowsAsync<RequestException>(() => control.Pause(id));

        Assert.Equal(WorkspaceState.Working, activity.Get(id));
    }

    [Fact]
    public async Task Pause_ReleasesTheWorkspaceWhenTheAiFinishedMeanwhile()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        prompts.Setup(x => x.IsBusy(id)).Returns(false);
        prompts.Setup(x => x.Pause(id)).ThrowsAsync(new RequestException("The AI is not working in this workspace."));

        await Assert.ThrowsAsync<RequestException>(() => control.Pause(id));

        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Theory]
    [InlineData(WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing)]
    [InlineData(WorkspaceState.Paused)]
    public async Task Pause_IsRefusedUnlessTheAiIsWorking(WorkspaceState held)
    {
        activity.TryEnter(id, held == WorkspaceState.Paused ? WorkspaceState.Working : held);

        if (held == WorkspaceState.Paused)
        {
            activity.Pause(id);
        }

        await Assert.ThrowsAsync<RequestException>(() => control.Pause(id));
        prompts.Verify(x => x.Pause(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Resume_IsRefusedUnlessPausedAndStopWorksOnAPausedAi()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        await Assert.ThrowsAsync<RequestException>(() => control.Resume(id));
        activity.Pause(id);

        await control.Stop(id);

        prompts.Verify(x => x.Interrupt(id), Times.Once);
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Fact]
    public async Task Restore_PutsPausedWorkBackWithItsSnapshotAndPrompts()
    {
        WorkspaceSnapshot snapshot = new() { Folder = "/work/app", Head = "abc", Branch = "main", IndexTree = "i", Tree = "t" };
        store.Setup(x => x.LoadPaused(It.IsAny<CancellationToken>())).ReturnsAsync([new PausedWork { WorkspaceId = id, Snapshot = snapshot, Prompts = ["go"] }, new PausedWork { WorkspaceId = Guid.NewGuid() }]);

        await control.Restore(CancellationToken.None);

        Assert.Equal(WorkspaceState.Paused, activity.Get(id));
        snapshots.Verify(x => x.Import(id, snapshot), Times.Once);
        prompts.Verify(x => x.Restore(id, It.Is<IReadOnlyList<string>>(list => list.SequenceEqual(new[] { "go" }))), Times.Once);
        prompts.Verify(x => x.Restore(It.Is<Guid>(other => other != id), It.IsAny<IReadOnlyList<string>>()), Times.Never);
    }

    [Fact]
    public async Task EveryChangeOfWhatIsPausedIsStoredAndNothingIsStoredOnceTheServerStops()
    {
        List<IReadOnlyList<PausedWork>> saved = [];
        store.Setup(x => x.SavePaused(It.IsAny<IReadOnlyList<PausedWork>>(), It.IsAny<CancellationToken>())).Callback<IReadOnlyList<PausedWork>, CancellationToken>((work, _) =>
        {
            lock (saved)
            {
                saved.Add(work);
            }
        }).Returns(Task.CompletedTask);
        snapshots.Setup(x => x.Export(id)).Returns(new WorkspaceSnapshot { Folder = "/work/app", IndexTree = "i", Tree = "t" });
        using CancellationTokenSource server = new();
        await control.Restore(server.Token);
        activity.TryEnter(id, WorkspaceState.Working);

        await control.Pause(id);
        await Until(() => saved.Count == 1);
        Assert.Equal(id, Assert.Single(saved[0]).WorkspaceId);
        Assert.Equal(["do it"], saved[0][0].Prompts);
        Assert.Equal("t", saved[0][0].Snapshot?.Tree);

        await control.Resume(id);
        await Until(() => saved.Count == 2);
        Assert.Empty(saved[1]);

        await control.Pause(id);
        await Until(() => saved.Count == 3);
        server.Cancel();
        await control.Stop(id);
        await Task.Delay(200);

        Assert.Equal(3, saved.Count);
    }

    private async Task Until(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(condition());
    }
}
