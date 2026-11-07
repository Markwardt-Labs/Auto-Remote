namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class ServerRestartTests
{
    private readonly CancellationTokenSource stopping = new();

    private readonly StringWriter output = new();

    private readonly Dictionary<string, string> variables = [];

    private readonly List<ProcessStartInfo> started = [];

    private readonly Mock<IExecutableInstaller> installer = new();

    private string? startFailure;

    private ServerRestart Create(string? executable = "/opt/server")
    {
        installer.SetupGet(x => x.Executable).Returns(executable);
        return new(stopping, output, installer.Object, ["config.json"], name => variables.GetValueOrDefault(name), info =>
    {
        if (info.FileName != "/opt/server")
        {
            return null;
        }

        started.Add(info);
        return startFailure;
    }, TimeSpan.FromMilliseconds(20));
    }

    [Fact]
    public async Task Request_CancelsTheServerAfterTheDelay()
    {
        ServerRestart restart = Create();

        restart.Request();

        Assert.True(restart.IsRequested);
        Assert.False(stopping.IsCancellationRequested);

        for (int attempt = 0; attempt < 250 && !stopping.IsCancellationRequested; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(stopping.IsCancellationRequested);
    }

    [Fact]
    public void Relaunch_ExercisesStartingAProcessThenPutsTheDownloadedExecutableInPlaceThenStartsIt()
    {
        List<string> order = [];
        installer.Setup(x => x.Apply()).Callback(() => order.Add("apply"));
        ServerRestart restart = new(stopping, output, installer.Object, ["config.json"], _ => null, info =>
        {
            order.Add(info.FileName == "/opt/server" ? "start" : "warm");
            return null;
        }, TimeSpan.FromMilliseconds(20));
        installer.SetupGet(x => x.Executable).Returns("/opt/server");

        restart.Relaunch();

        Assert.Equal(["warm", "apply", "start"], order);
    }

    [Fact]
    public void Relaunch_UnderAServiceManagerAlsoAppliesTheUpdateFirst()
    {
        variables["INVOCATION_ID"] = "abc";

        Create().Relaunch();

        installer.Verify(x => x.Apply(), Times.Once);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(InvalidOperationException))]
    public void Relaunch_StartsTheOldVersionAgainWhenTheUpdateCannotBePutInPlace(Type failure)
    {
        installer.Setup(x => x.Apply()).Throws((Exception)Activator.CreateInstance(failure, "locked")!);

        int code = Create().Relaunch();

        Assert.Equal(0, code);
        Assert.Single(started);
        Assert.Contains("old version starts again: locked", output.ToString());
    }

    [Fact]
    public void Relaunch_UnderAServiceManagerExitsWithAFailureCodeAndStartsNothing()
    {
        variables["INVOCATION_ID"] = "abc";

        int code = Create().Relaunch();

        Assert.Equal(75, code);
        Assert.Empty(started);
    }

    [Fact]
    public void Relaunch_WithoutAServiceManagerStartsTheNewExecutableWithTheSameArguments()
    {
        int code = Create().Relaunch();

        Assert.Equal(0, code);
        ProcessStartInfo info = Assert.Single(started);
        Assert.Equal("/opt/server", info.FileName);
        Assert.Equal(["config.json"], info.ArgumentList);
        Assert.False(info.UseShellExecute);
    }

    [Fact]
    public void Relaunch_ReportsWhenTheNewExecutableCannotBeStarted()
    {
        startFailure = "no such file";

        Assert.Equal(1, Create().Relaunch());
        Assert.Contains("could not be started", output.ToString());
        Assert.Contains("no such file", output.ToString());
    }

    [Fact]
    public void Relaunch_WithoutAnExecutableLeavesTheStartToTheUser()
    {
        Assert.Equal(0, Create(null).Relaunch());
        Assert.Empty(started);
        Assert.Contains("by hand", output.ToString());
    }
}
