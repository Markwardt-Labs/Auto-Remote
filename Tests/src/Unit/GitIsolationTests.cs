namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class GitIsolationTests
{
    [Fact]
    public void TheTestsNeverUseTheGitConfigurationOfTheUserWhoRunsThem()
        => Assert.StartsWith(Path.Combine(Path.GetTempPath(), "autoremote-tests-"), Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL"));
}
