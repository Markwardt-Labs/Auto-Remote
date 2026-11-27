namespace Markwardt.AutoRemote.Tests;

/// <summary>
/// Gives the whole test run a global git configuration of its own, so that nothing a test does can read or change the one of the user who runs it. Setting a git identity through the server writes the global configuration.
/// </summary>
internal static class GitIsolation
{
    /// <summary>
    /// Gets the configuration file the tests and the git processes they start use.
    /// </summary>
    internal static string ConfigFile { get; } = Path.Combine(Path.GetTempPath(), $"autoremote-tests-{Environment.ProcessId}.gitconfig");

    /// <summary>
    /// Points git at it before any test runs.
    /// </summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255:The ModuleInitializer attribute should not be used in libraries", Justification = "It has to happen before any test of the assembly runs.")]
    internal static void Isolate()
    {
        File.WriteAllText(ConfigFile, "[user]\n\tname = Test\n\temail = test@example.com\n[init]\n\tdefaultBranch = main\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", ConfigFile);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => File.Delete(ConfigFile);
    }
}
