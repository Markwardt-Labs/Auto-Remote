namespace Markwardt.AutoRemote;

/// <summary>
/// Replaces the program that is running with the executable of a release in two steps, so that nothing changes under the running program until it has finished everything else it does: <see cref="Stage" /> downloads, <see cref="Apply" /> swaps.
/// </summary>
/// <remarks>
/// After <see cref="Apply" /> the running process must not load any assembly it has not loaded yet: a single-file program reads its assemblies from the executable on disk, which is the new release by then. Callers therefore finish loading what they need before they apply.
/// </remarks>
public interface IExecutableInstaller
{
    /// <summary>
    /// Gets the path of the running self-contained executable, or <see langword="null" /> when the program does not run from one, such as when it is started with <c>dotnet run</c>, which cannot be replaced.
    /// </summary>
    string? Executable { get; }

    /// <summary>
    /// Downloads a file of a release next to the running executable, ready to be applied.
    /// </summary>
    /// <param name="release">The release to install.</param>
    /// <param name="assetName">The name of the release file for this platform.</param>
    /// <param name="progress">Receives the fraction downloaded, from 0 to 1, or <see langword="null" /> to ignore it.</param>
    /// <param name="cancellation">Cancels the download.</param>
    /// <exception cref="InvalidOperationException">The program does not run from an executable that can be replaced.</exception>
    /// <exception cref="IOException">The release has no such file or the download failed; nothing is left behind.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder of the executable is not writable.</exception>
    Task Stage(ReleaseInfo release, string assetName, IProgress<double>? progress, CancellationToken cancellation);

    /// <summary>
    /// Puts the staged file in place of the running executable, which takes effect the next time the program starts.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing is staged.</exception>
    /// <exception cref="IOException">The file could not be put in place; the running executable is left as it was.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder of the executable is not writable.</exception>
    void Apply();

    /// <summary>
    /// Deletes the leftovers of an earlier installation next to the executable, which Windows cannot delete while the old program is still running.
    /// </summary>
    void Clean();

    /// <summary>
    /// Creates <see cref="IExecutableInstaller" /> instances.
    /// </summary>
    public interface IFactory
    {
        /// <summary>
        /// Creates the installer of the running program.
        /// </summary>
        /// <param name="feed">Downloads the release files.</param>
        /// <returns>The installer.</returns>
        IExecutableInstaller Create(IReleaseFeed feed);
    }

    /// <inheritdoc />
    public sealed class Factory : IFactory
    {
        /// <inheritdoc />
        public IExecutableInstaller Create(IReleaseFeed feed) => new ExecutableInstaller(feed, Environment.ProcessPath, OperatingSystem.IsWindows());
    }
}

/// <inheritdoc />
internal sealed class ExecutableInstaller(IReleaseFeed feed, string? processPath, bool windows) : IExecutableInstaller
{
    /// <inheritdoc />
    public string? Executable { get; } = processPath is not null && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase) ? processPath : null;

    /// <inheritdoc />
    public async Task Stage(ReleaseInfo release, string assetName, IProgress<double>? progress, CancellationToken cancellation)
    {
        string executable = Executable ?? throw new InvalidOperationException("This program does not run from an installed release, so it cannot replace itself.");
        ReleaseAsset asset = release.Assets.FirstOrDefault(candidate => candidate.Name == assetName) ?? throw new IOException($"Release {release.Version} has no {assetName} file.");
        string staged = $"{executable}.new";
        await feed.Download(asset, staged, progress, cancellation);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(staged, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        }
    }

    /// <inheritdoc />
    public void Apply()
    {
        string executable = Executable ?? throw new InvalidOperationException("This program does not run from an installed release, so it cannot replace itself.");
        string staged = $"{executable}.new";

        if (!File.Exists(staged))
        {
            throw new InvalidOperationException("No update is staged.");
        }

        if (!windows)
        {
            File.Move(staged, executable, true);
            return;
        }

        string previous = $"{executable}.old";
        File.Delete(previous);
        File.Move(executable, previous);

        try
        {
            File.Move(staged, executable);
        }
        catch
        {
            File.Move(previous, executable);
            throw;
        }
    }

    /// <inheritdoc />
    public void Clean()
    {
        if (Executable is { } executable)
        {
            Discard($"{executable}.old");
            Discard($"{executable}.new");
        }
    }

    private void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
