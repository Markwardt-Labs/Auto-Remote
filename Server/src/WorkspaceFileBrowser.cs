namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Gives read-only access to the files inside a workspace's folder.
/// </summary>
internal interface IWorkspaceFileBrowser
{
    /// <summary>
    /// Lists a folder inside the workspace folder.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="path">The folder, relative to the workspace folder; empty for the workspace folder.</param>
    /// <param name="cancellation">Cancels the listing.</param>
    /// <returns>The folder's entries.</returns>
    /// <exception cref="RequestException">The path leaves the workspace folder, is missing, or cannot be read.</exception>
    Task<WorkspaceFiles> List(WorkspaceInfo workspace, string path, CancellationToken cancellation);

    /// <summary>
    /// Reads a text file inside the workspace folder.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="path">The file, relative to the workspace folder.</param>
    /// <param name="cancellation">Cancels the read.</param>
    /// <returns>The file text, truncated when large.</returns>
    /// <exception cref="RequestException">The path leaves the workspace folder, is missing, or is not a text file.</exception>
    Task<FileContent> Read(WorkspaceInfo workspace, string path, CancellationToken cancellation);

    /// <summary>
    /// Watches a folder inside the workspace folder, not its sub-folders.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <param name="path">The folder, relative to the workspace folder; empty for the workspace folder.</param>
    /// <param name="changed">Called with the folder's relative path shortly after its entries or their contents changed.</param>
    /// <returns>Stops watching when disposed.</returns>
    /// <exception cref="RequestException">The path leaves the workspace folder or is not a folder.</exception>
    IDisposable Watch(WorkspaceInfo workspace, string path, Action<string> changed);
}

/// <inheritdoc />
internal sealed class WorkspaceFileBrowser : IWorkspaceFileBrowser
{
    private readonly int maxBytes = 256 * 1024;

    private readonly TimeSpan debounce = TimeSpan.FromMilliseconds(250);

    private readonly StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public Task<WorkspaceFiles> List(WorkspaceInfo workspace, string path, CancellationToken cancellation) => Task.Run(
        () =>
        {
            string root = Path.TrimEndingDirectorySeparator(workspace.Folder);
            string full = Resolve(root, path);

            if (!Directory.Exists(full))
            {
                throw new RequestException($"The folder '{path}' does not exist.");
            }

            try
            {
                List<FileEntry> entries = [.. new DirectoryInfo(full).EnumerateFileSystemInfos()
                    .Select(info => (info, folder: info.Attributes.HasFlag(FileAttributes.Directory)))
                    .OrderByDescending(item => item.folder)
                    .ThenBy(item => item.info.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(item => new FileEntry { Name = item.info.Name, Path = Relative(root, item.info.FullName), IsFolder = item.folder, Size = item.info is FileInfo file ? file.Length : 0 })];

                string relative = Relative(root, full);
                return new WorkspaceFiles { Path = relative, Parent = relative.Length == 0 ? null : Relative(root, Path.GetDirectoryName(full)!), Entries = entries };
            }
            catch (UnauthorizedAccessException)
            {
                throw new RequestException($"Access to '{path}' is denied.");
            }
        },
        cancellation);

    /// <inheritdoc />
    public async Task<FileContent> Read(WorkspaceInfo workspace, string path, CancellationToken cancellation)
    {
        string root = Path.TrimEndingDirectorySeparator(workspace.Folder);
        string full = Resolve(root, path);

        if (!File.Exists(full))
        {
            throw new RequestException($"The file '{path}' does not exist.");
        }

        try
        {
            await using FileStream stream = new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
            byte[] buffer = new byte[maxBytes + 1];
            int read = 0;

            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), cancellation) is > 0 and int count)
            {
                read += count;
            }

            if (buffer.AsSpan(0, Math.Min(read, 8192)).Contains((byte)0))
            {
                throw new RequestException("This file is not a text file.");
            }

            bool truncated = read > maxBytes;
            return new FileContent { Path = Relative(root, full), Text = Encoding.UTF8.GetString(buffer, 0, Math.Min(read, maxBytes)), Truncated = truncated };
        }
        catch (UnauthorizedAccessException)
        {
            throw new RequestException($"Access to '{path}' is denied.");
        }
    }

    /// <inheritdoc />
    public IDisposable Watch(WorkspaceInfo workspace, string path, Action<string> changed)
    {
        string root = Path.TrimEndingDirectorySeparator(workspace.Folder);
        string full = Resolve(root, path);

        if (!Directory.Exists(full))
        {
            throw new RequestException($"The folder '{path}' does not exist.");
        }

        return new Watcher(full, Relative(root, full), changed, debounce);
    }

    private string Resolve(string root, string path)
    {
        string full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar)));

        if (!string.Equals(full, root, comparison) && !full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new RequestException("That path is outside the workspace folder.");
        }

        return full;
    }

    private string Relative(string root, string full) => Path.GetRelativePath(root, full) is var relative && relative == "." ? string.Empty : relative.Replace(Path.DirectorySeparatorChar, '/');

    private sealed class Watcher : IDisposable
    {
        public Watcher(string full, string relative, Action<string> changed, TimeSpan debounce)
        {
            this.debounce = debounce;
            timer = new Timer(_ => Notify(relative, changed));
            watcher = new FileSystemWatcher(full) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Created += (_, _) => Schedule();
            watcher.Deleted += (_, _) => Schedule();
            watcher.Changed += (_, _) => Schedule();
            watcher.Renamed += (_, _) => Schedule();
            watcher.Error += (_, _) => Schedule();
            watcher.EnableRaisingEvents = true;
        }

        private readonly TimeSpan debounce;

        private readonly Timer timer;

        private readonly FileSystemWatcher watcher;

        private volatile bool disposed;

        public void Dispose()
        {
            disposed = true;
            watcher.Dispose();
            timer.Dispose();
        }

        private void Schedule()
        {
            if (!disposed)
            {
                timer.Change(debounce, Timeout.InfiniteTimeSpan);
            }
        }

        private void Notify(string relative, Action<string> changed)
        {
            if (!disposed)
            {
                changed(relative);
            }
        }
    }
}
