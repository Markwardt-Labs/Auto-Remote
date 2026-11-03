namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceControlTests
{
    private readonly Mock<IPromptService> prompts = new();

    private readonly Mock<IScriptRunner> scripts = new();

    private readonly Mock<IWorkspaceVersioning> versioning = new();

    private readonly Mock<IWorkspaceSnapshots> snapshots = new();

    private readonly EventHub hub = new();

    private readonly WorkspaceActivity activity;

    private readonly WorkspaceControl control;

    private readonly Guid id = Guid.NewGuid();

    public WorkspaceControlTests()
    {
        activity = new WorkspaceActivity(hub);
        control = new WorkspaceControl(activity, prompts.Object, scripts.Object, versioning.Object, snapshots.Object);
        snapshots.Setup(x => x.Has(id)).Returns(true);
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

    [Fact]
    public async Task Cancel_StopsTheAiThenRevertsItWhileCanceling()
    {
        activity.TryEnter(id, WorkspaceState.Working);
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
}
