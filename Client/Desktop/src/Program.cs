namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The entry point of the desktop client.
/// </summary>
public static class Program
{
    /// <summary>
    /// Starts the desktop client.
    /// </summary>
    /// <param name="args">The command line arguments.</param>
    /// <returns>The exit code of the application.</returns>
    public static int Main(string[] args) => AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}
