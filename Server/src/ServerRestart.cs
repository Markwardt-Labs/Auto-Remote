namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Shuts the server down so that a new version can take over.
/// </summary>
internal interface IServerRestart
{
    /// <summary>
    /// Gets a value indicating whether a restart was requested.
    /// </summary>
    bool IsRequested { get; }

    /// <summary>
    /// Asks for the restart: after a short delay, which lets the answer to the request that asked reach its client, the server is cancelled and shuts down.
    /// </summary>
    void Request();

    /// <summary>
    /// Brings the server up again once it has shut down: first it starts a harmless process, so that everything starting one needs is loaded while the executable it is read from is still the running one, then the downloaded executable is put in place of the running one, then the new version is started. Under a service manager, which sets <c>INVOCATION_ID</c>, the server exits with a failure code so that the manager's restart on failure policy starts the new version, because the manager would stop anything the server started itself; otherwise the new executable is started as a process of its own with the same command line.
    /// </summary>
    /// <returns>The exit code of this process.</returns>
    int Relaunch();
}

/// <inheritdoc />
internal sealed class ServerRestart(CancellationTokenSource stopping, TextWriter output, IExecutableInstaller installer, IReadOnlyList<string> arguments, Func<string, string?> environment, Func<ProcessStartInfo, string?> start, TimeSpan delay) : IServerRestart
{
    private volatile bool requested;

    /// <inheritdoc />
    public bool IsRequested => requested;

    /// <inheritdoc />
    public void Request()
    {
        requested = true;
        _ = Cancel();
    }

    /// <inheritdoc />
    public int Relaunch()
    {
        Warm();

        try
        {
            installer.Apply();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            output.WriteLine($"The update could not be put in place, so the old version starts again: {exception.Message}");
        }

        if (!string.IsNullOrEmpty(environment("INVOCATION_ID")))
        {
            output.WriteLine("Exiting so that the service manager starts the new version");
            return 75;
        }

        if (installer.Executable is not { } executable)
        {
            output.WriteLine("The new version is installed, but this server was not started from an executable, so it has to be started again by hand");
            return 0;
        }

        ProcessStartInfo info = new(executable) { UseShellExecute = false };

        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (start(info) is { } failure)
        {
            output.WriteLine($"The new version is installed, but it could not be started, so it has to be started again by hand: {failure}");
            return 1;
        }

        output.WriteLine("Started the new version");
        return 0;
    }

    private void Warm()
    {
        ProcessStartInfo harmless = OperatingSystem.IsWindows() ? new("cmd.exe") { ArgumentList = { "/c", "exit" } } : new("/bin/true");
        harmless.UseShellExecute = false;
        harmless.CreateNoWindow = true;
        _ = start(harmless);
        RuntimeHelpers.PrepareMethod(start.Method.MethodHandle);
    }

    private async Task Cancel()
    {
        await Task.Delay(delay);
        await stopping.CancelAsync();
    }
}
