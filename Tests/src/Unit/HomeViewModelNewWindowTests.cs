namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class HomeViewModelNewWindowTests
{
    private readonly Mock<IServerConnection> connection = new();

    private readonly Mock<INewWindow> newWindow = new();

    private HomeViewModel Model(bool withNewWindow)
        => new(connection.Object, ClientLayout.Panels, null, null, null, null, null, null, withNewWindow ? newWindow.Object : null);

    [Fact]
    public void CanOpenNewWindow_IsTrueOnlyForAClientThatCanDoIt()
    {
        Assert.True(Model(true).CanOpenNewWindow);
        Assert.False(Model(false).CanOpenNewWindow);
    }

    [Fact]
    public async Task NewWindowCommand_OpensAnotherWindowEachTimeItIsUsed()
    {
        HomeViewModel model = Model(true);

        model.NewWindowCommand.Execute(null);
        model.NewWindowCommand.Execute(null);
        await Task.Delay(50);

        newWindow.Verify(x => x.Open(), Times.Exactly(2));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task NewWindowCommand_ShowsWhyAWindowCouldNotBeOpened()
    {
        newWindow.Setup(x => x.Open()).Throws(new InvalidOperationException("A new window could not be opened: no program."));
        HomeViewModel model = Model(true);

        model.NewWindowCommand.Execute(null);
        await Task.Delay(50);

        Assert.Equal("A new window could not be opened: no program.", model.Error);
    }
}
