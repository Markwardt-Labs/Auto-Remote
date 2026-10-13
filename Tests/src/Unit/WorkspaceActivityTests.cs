namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class WorkspaceActivityTests
{
    private readonly WorkspaceActivity activity = new();

    private readonly Guid id = Guid.NewGuid();

    [Fact]
    public void Get_IsReadyUntilWorkIsEntered()
    {
        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
        Assert.Equal(WorkspaceState.Ready, activity.Get(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(WorkspaceState.Working)]
    [InlineData(WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing)]
    public void TryEnter_FromReady_TakesTheWorkspaceUntilItLeaves(WorkspaceState state)
    {
        Assert.True(activity.TryEnter(id, state));
        Assert.Equal(state, activity.Get(id));

        activity.Leave(id, state);

        Assert.Equal(WorkspaceState.Ready, activity.Get(id));
    }

    [Theory]
    [InlineData(WorkspaceState.Working, WorkspaceState.Running)]
    [InlineData(WorkspaceState.Working, WorkspaceState.Managing)]
    [InlineData(WorkspaceState.Running, WorkspaceState.Working)]
    [InlineData(WorkspaceState.Running, WorkspaceState.Managing)]
    [InlineData(WorkspaceState.Running, WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing, WorkspaceState.Running)]
    [InlineData(WorkspaceState.Managing, WorkspaceState.Managing)]
    public void TryEnter_FromAnotherState_IsRefusedAndKeepsTheState(WorkspaceState held, WorkspaceState wanted)
    {
        activity.TryEnter(id, held);

        Assert.False(activity.TryEnter(id, wanted));
        Assert.Equal(held, activity.Get(id));
    }

    [Fact]
    public void TryEnter_Working_JoinsWorkingAndNestsInManaging()
    {
        activity.TryEnter(id, WorkspaceState.Working);
        Assert.True(activity.TryEnter(id, WorkspaceState.Working));
        activity.Leave(id, WorkspaceState.Working);

        activity.TryEnter(id, WorkspaceState.Managing);

        Assert.True(activity.TryEnter(id, WorkspaceState.Working));
        Assert.Equal(WorkspaceState.Managing, activity.Get(id));
        activity.Leave(id, WorkspaceState.Working);
        Assert.Equal(WorkspaceState.Managing, activity.Get(id));
    }

    [Fact]
    public void Leave_OfAnotherState_ChangesNothing()
    {
        activity.TryEnter(id, WorkspaceState.Running);

        activity.Leave(id, WorkspaceState.Working);

        Assert.Equal(WorkspaceState.Running, activity.Get(id));
    }

    [Theory]
    [InlineData(WorkspaceState.Working, "AI is working")]
    [InlineData(WorkspaceState.Running, "script is running")]
    [InlineData(WorkspaceState.Managing, "git action is running")]
    public void Describe_NamesTheWorkInProgress(WorkspaceState state, string text)
    {
        activity.TryEnter(id, state);

        Assert.Contains(text, activity.Describe(id));
    }
}
