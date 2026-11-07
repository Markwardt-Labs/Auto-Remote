namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Updates the desktop client by downloading the executable of the release, putting it in place of its own, starting it and ending this process. Other windows of the app are processes of their own and keep running the old version until they are closed.
/// </summary>
/// <param name="installer">Downloads the executable of the release and puts it in place of the running one.</param>
/// <param name="restart">Starts the new executable with the command line of this one.</param>
internal sealed class DesktopUpdater(IExecutableInstaller installer, INewWindow restart) : IClientUpdater
{
    /// <inheritdoc />
    public bool IsSupported => installer.Executable is not null && Asset is not null;

    private string? Asset
    {
        get
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                return null;
            }

            return OperatingSystem.IsWindows() ? "AutoRemote-win.exe" : OperatingSystem.IsLinux() ? "AutoRemote-linux" : null;
        }
    }

    /// <inheritdoc />
    public async Task Install(ReleaseInfo release, IProgress<double> progress, CancellationToken cancellation)
    {
        await installer.Stage(release, Asset ?? throw new IOException("There is no release build of the client for this platform."), progress, cancellation);
        Warm();
        installer.Apply();
        restart.Open();
        Environment.Exit(0);
    }

    private void Warm()
    {
        ProcessStartInfo harmless = OperatingSystem.IsWindows() ? new("cmd.exe") { ArgumentList = { "/c", "exit" } } : new("/bin/true");
        harmless.UseShellExecute = false;
        harmless.CreateNoWindow = true;

        try
        {
            Process.Start(harmless)?.Dispose();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
        }
    }
}
