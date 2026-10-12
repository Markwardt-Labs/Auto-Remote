namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Browses and manages folders on the server's machine.
/// </summary>
internal interface IFolderBrowser
{
    /// <summary>
    /// Sets the folder that listing without a path starts in.
    /// </summary>
    /// <param name="defaultFolder">The default folder.</param>
    void Configure(string defaultFolder);

    /// <summary>
    /// Lists the visible sub-folders of a folder.
    /// </summary>
    /// <param name="path">The folder; <see langword="null" /> for the default folder, empty for the file system roots.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The listing.</returns>
    /// <exception cref="RequestException">The folder does not exist or cannot be read.</exception>
    Task<FolderListing> List(string? path, CancellationToken cancellation);

    /// <summary>
    /// Creates a folder.
    /// </summary>
    /// <param name="parent">The existing folder to create it in.</param>
    /// <param name="name">The new folder's name.</param>
    /// <param name="cancellation">Cancels the creation.</param>
    /// <returns>The listing of the new folder.</returns>
    /// <exception cref="RequestException">The name is invalid, the parent is missing, or the folder already exists.</exception>
    Task<FolderListing> Create(string parent, string name, CancellationToken cancellation);

    /// <summary>
    /// Renames a folder in place.
    /// </summary>
    /// <param name="path">The existing folder.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellation">Cancels the rename.</param>
    /// <returns>The folder's new absolute path.</returns>
    /// <exception cref="RequestException">The name is invalid, the folder is missing, or the target already exists.</exception>
    Task<string> Rename(string path, string name, CancellationToken cancellation);

    /// <summary>
    /// Deletes a folder and everything in it; does nothing if it is already gone.
    /// </summary>
    /// <param name="path">The folder to delete.</param>
    /// <param name="cancellation">Cancels the deletion.</param>
    /// <exception cref="RequestException">The folder is a file system root or the user's home folder (or contains it).</exception>
    Task Delete(string path, CancellationToken cancellation);

    /// <summary>
    /// Checks whether a folder exists.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <param name="cancellation">Cancels the check.</param>
    /// <returns><see langword="true" /> if the folder exists.</returns>
    Task<bool> Exists(string path, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class FolderBrowser : IFolderBrowser
{
    private readonly string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string defaultFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <inheritdoc />
    public void Configure(string defaultFolder) => this.defaultFolder = defaultFolder;

    /// <inheritdoc />
    public Task<FolderListing> List(string? path, CancellationToken cancellation) => Task.Run(() => Describe(path ?? (defaultFolder.Length > 0 ? defaultFolder : Path.GetPathRoot(Environment.CurrentDirectory) ?? "/")), cancellation);

    /// <inheritdoc />
    public Task<FolderListing> Create(string parent, string name, CancellationToken cancellation) => Task.Run(
        () =>
        {
            ValidateName(name);
            string full = Path.Combine(RequireFolder(parent), name.Trim());

            if (Path.Exists(full))
            {
                throw new RequestException($"'{name.Trim()}' already exists.");
            }

            Directory.CreateDirectory(full);
            return Describe(full);
        },
        cancellation);

    /// <inheritdoc />
    public Task<string> Rename(string path, string name, CancellationToken cancellation) => Task.Run(
        () =>
        {
            ValidateName(name);
            string source = Path.TrimEndingDirectorySeparator(RequireFolder(path));
            string target = Path.Combine(Path.GetDirectoryName(source) ?? throw new RequestException("A root folder cannot be renamed."), name.Trim());

            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                return source;
            }

            if (Path.Exists(target))
            {
                throw new RequestException($"'{name.Trim()}' already exists.");
            }

            Directory.Move(source, target);
            return target;
        },
        cancellation);

    /// <inheritdoc />
    public Task Delete(string path, CancellationToken cancellation) => Task.Run(
        () =>
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string homeFull = Path.TrimEndingDirectorySeparator(home);

            if (Path.GetPathRoot(full) is { } root && Path.TrimEndingDirectorySeparator(root).Length == full.Length
                || full.Length == 0
                || homeFull.StartsWith(full + Path.DirectorySeparatorChar, comparison)
                || string.Equals(homeFull, full, comparison))
            {
                throw new RequestException("This folder is too important to delete.");
            }

            if (Directory.Exists(full))
            {
                Directory.Delete(full, true);
            }
        },
        cancellation);

    /// <inheritdoc />
    public Task<bool> Exists(string path, CancellationToken cancellation) => Task.Run(() => Directory.Exists(path), cancellation);

    private FolderListing Describe(string path)
    {
        if (path.Length == 0 && OperatingSystem.IsWindows())
        {
            return new FolderListing { Path = string.Empty, Folders = [.. Directory.GetLogicalDrives().Select(drive => new FolderEntry { Name = drive, Path = drive })] };
        }

        string full = Path.GetFullPath(path.Length == 0 ? "/" : path);
        DirectoryInfo directory = new(RequireFolder(full));

        try
        {
            List<FolderEntry> folders = [.. directory.EnumerateDirectories()
                .Where(child => !child.Name.StartsWith('.') && !child.Attributes.HasFlag(FileAttributes.Hidden))
                .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
                .Select(child => new FolderEntry { Name = child.Name, Path = child.FullName })];

            return new FolderListing { Path = directory.FullName, Parent = directory.Parent?.FullName ?? (OperatingSystem.IsWindows() ? string.Empty : null), Folders = folders };
        }
        catch (UnauthorizedAccessException)
        {
            throw new RequestException($"Access to '{directory.FullName}' is denied.");
        }
    }

    private string RequireFolder(string path) => Directory.Exists(path) ? Path.GetFullPath(path) : throw new RequestException($"The folder '{path}' does not exist.");

    private void ValidateName(string name)
    {
        string trimmed = name.Trim();

        if (trimmed.Length == 0 || trimmed is "." or ".." || trimmed.IndexOfAny(['/', '\\']) >= 0 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new RequestException("The folder name is not valid.");
        }
    }
}
