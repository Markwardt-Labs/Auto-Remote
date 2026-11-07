namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Updates the server to the newest release on GitHub.
/// </summary>
internal interface IServerUpdater
{
    /// <summary>
    /// Downloads the newest release next to the running executable and requests a restart, which puts it in place once the server has shut down.
    /// </summary>
    /// <param name="cancellation">Cancels the download.</param>
    /// <returns>A task that completes once the new executable is downloaded; the restart follows shortly after.</returns>
    /// <exception cref="RequestException">There is no newer release, there is no build for this platform, the server does not run from an installed release or an update is already running.</exception>
    /// <exception cref="IOException">GitHub could not be reached, or the download or installation failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder of the executable is not writable.</exception>
    Task Update(CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class ServerUpdater(IAppInfo info, IReleaseFeed feed, IExecutableInstaller installer, IServerRestart restart) : IServerUpdater
{
    private readonly SemaphoreSlim updating = new(1, 1);

    /// <inheritdoc />
    public async Task Update(CancellationToken cancellation)
    {
        if (!await updating.WaitAsync(0, cancellation))
        {
            throw new RequestException("The server is already updating.");
        }

        try
        {
            if (restart.IsRequested)
            {
                throw new RequestException("The server is already restarting with an update.");
            }

            if (installer.Executable is null)
            {
                throw new RequestException("The server does not run from an installed release, so it cannot update itself.");
            }

            string asset = AssetName();
            ReleaseInfo release = await feed.Latest(cancellation) ?? throw new RequestException("No release was found on GitHub.");

            if (!AppVersion.TryParse(info.Version, out AppVersion? current) || release.Version > current)
            {
                await installer.Stage(release, asset, null, cancellation);
                restart.Request();
                return;
            }

            throw new RequestException($"The server is up to date at {info.Version}.");
        }
        finally
        {
            updating.Release();
        }
    }

    private string AssetName()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            throw new RequestException("There is no release build of the server for this platform.");
        }

        return OperatingSystem.IsWindows() ? "AutoRemote-Server-win.exe" : "AutoRemote-Server-linux";
    }
}
