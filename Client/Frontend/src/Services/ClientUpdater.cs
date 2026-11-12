namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Installs a release of the app over the running one, which only a head that is installed on its own can do: the desktop head replaces its executable and starts the new one, the Android head hands the downloaded package to the system installer.
/// </summary>
public interface IClientUpdater
{
    /// <summary>
    /// Gets a value indicating whether this run of the app can be replaced, which is not the case when it was started from a development build; without it the client offers no update for itself.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Downloads the file of the release for this platform and installs it. On the desktop the new version is started and this process ends once it is in place; on Android the call returns once the system installer is showing.
    /// </summary>
    /// <param name="release">The release to install.</param>
    /// <param name="progress">Receives the fraction downloaded, from 0 to 1.</param>
    /// <param name="downloaded">Awaited once the download is complete, before the app is replaced or the installer is shown, which is the last moment a desktop client can still write down what it was doing.</param>
    /// <param name="cancellation">Cancels the installation before anything is replaced.</param>
    /// <exception cref="IOException">The release has no file for this platform or the download or installation failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder of the app is not writable.</exception>
    /// <exception cref="InvalidOperationException">This run of the app cannot be replaced.</exception>
    Task Install(ReleaseInfo release, IProgress<double> progress, Func<Task> downloaded, CancellationToken cancellation);
}
