namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class AppInfoTests
{
    [Fact]
    public void Create_ReportsNameAndAssemblyVersion()
    {
        IAppInfo.IFactory factory = new IAppInfo.Factory();

        IAppInfo info = factory.Create(typeof(IAppInfo).Assembly);

        Assert.Equal("Auto Remote", info.Name);
        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Version);
    }
}
