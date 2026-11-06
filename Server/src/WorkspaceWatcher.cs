namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Watches the whole folder of every workspace that a client has open, and reports what changed in it to every client, so that changes made outside Auto Remote show up as well.
/// </summary>
internal interface IWorkspaceWatcher
{
    /// <summary>
    /// Starts watching a workspace folder, or joins the watch another client already has on it.
    /// </summary>
    /// <param name="workspace">The workspace.</param>
    /// <returns>Leaves the watch when disposed; the folder is watched for as long as anyone has not left.</returns>
    IDisposable Watch(WorkspaceInfo workspace);

    /// <summary>
    /// Stops watching a workspace folder for everyone, as when the workspace is renamed or deleted and its folder is no longer the one that was watched.
    /// </summary>
    /// <param name="id">The workspace identifier.</param>
    void Forget(Guid id);
}

/// <inheritdoc />
internal sealed class WorkspaceWatcher(IEventHub events, ILog log, TimeSpan? quietTime = null, TimeSpan? longestWait = null) : IWorkspaceWatcher
{
    private readonly IEventHub hub = events;

    private readonly ILog logger = log;

    private readonly Dictionary<Guid, Entry> entries = [];

    private readonly Lock gate = new();

    private readonly TimeSpan quiet = quietTime ?? TimeSpan.FromMilliseconds(400);

    private readonly TimeSpan longest = longestWait ?? TimeSpan.FromSeconds(2);

    private readonly int maxFolders = 200;

    private readonly string[] gitFiles = [".git/HEAD", ".git/index", ".git/packed-refs", ".git/MERGE_HEAD", ".git/REBASE_HEAD", ".git/CHERRY_PICK_HEAD"];

    /// <inheritdoc />
    public IDisposable Watch(WorkspaceInfo workspace)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(workspace.Id, out Entry? entry) || entry.Folder != workspace.Folder)
            {
                entry?.Stop();
                entry = new Entry(workspace.Id, workspace.Folder, this);
                entries[workspace.Id] = entry;
            }

            entry.Count++;
            return new Lease(this, entry);
        }
    }

    /// <inheritdoc />
    public void Forget(Guid id)
    {
        lock (gate)
        {
            if (entries.Remove(id, out Entry? entry))
            {
                entry.Stop();
            }
        }
    }

    private void Leave(Entry entry)
    {
        lock (gate)
        {
            if (--entry.Count <= 0 && entries.TryGetValue(entry.Id, out Entry? current) && current == entry)
            {
                entries.Remove(entry.Id);
                entry.Stop();
            }
        }
    }

    private sealed class Lease(WorkspaceWatcher owner, Entry entry) : IDisposable
    {
        private int left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref left, 1) == 0)
            {
                owner.Leave(entry);
            }
        }
    }

    private sealed class Entry
    {
        public Entry(Guid id, string folder, WorkspaceWatcher owner)
        {
            Id = id;
            Folder = folder;
            this.owner = owner;
            root = Path.TrimEndingDirectorySeparator(folder);
            timer = new Timer(_ => Flush());

            try
            {
                watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, InternalBufferSize = 65536, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                watcher.Created += (_, args) => Add(args.FullPath);
                watcher.Deleted += (_, args) => Add(args.FullPath);
                watcher.Changed += (_, args) => Add(args.FullPath);
                watcher.Renamed += (_, args) =>
                {
                    Add(args.OldFullPath);
                    Add(args.FullPath);
                };
                watcher.Error += (_, _) => Overflow();
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            {
                owner.logger.Write($"Could not watch {root}: {exception.Message}");
                watcher?.Dispose();
                watcher = null;
            }
        }

        private readonly WorkspaceWatcher owner;

        private readonly string root;

        private readonly Timer timer;

        private readonly FileSystemWatcher? watcher;

        private readonly HashSet<string> folders = [];

        private readonly Lock pending = new();

        private DateTimeOffset? first;

        private bool everything;

        private bool git;

        private bool stopped;

        public int Count { get; set; }

        public Guid Id { get; }

        public string Folder { get; }

        public void Stop()
        {
            lock (pending)
            {
                stopped = true;
            }

            watcher?.Dispose();
            timer.Dispose();
        }

        private void Add(string full)
        {
            string relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');

            if (relative == ".")
            {
                return;
            }

            if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                if (owner.gitFiles.Contains(relative) || relative.StartsWith(".git/refs/", StringComparison.Ordinal) && !relative.EndsWith(".lock", StringComparison.Ordinal))
                {
                    Schedule(null, true);
                }

                return;
            }

            int slash = relative.LastIndexOf('/');
            Schedule(slash < 0 ? string.Empty : relative[..slash], false);
        }

        private void Overflow() => Schedule(null, false, true);

        private void Schedule(string? folder, bool gitChanged, bool all = false)
        {
            lock (pending)
            {
                if (stopped)
                {
                    return;
                }

                git |= gitChanged;
                everything |= all;

                if (folder is not null && !everything)
                {
                    folders.Add(folder);

                    if (folders.Count > owner.maxFolders)
                    {
                        everything = true;
                    }
                }

                if (everything)
                {
                    folders.Clear();
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                first ??= now;
                TimeSpan left = first.Value + owner.longest - now;
                timer.Change(left < owner.quiet ? (left < TimeSpan.Zero ? TimeSpan.Zero : left) : owner.quiet, Timeout.InfiniteTimeSpan);
            }
        }

        private void Flush()
        {
            string[] changed;
            bool all;
            bool gitChanged;

            lock (pending)
            {
                if (stopped)
                {
                    return;
                }

                changed = [.. folders];
                all = everything;
                gitChanged = git;
                folders.Clear();
                everything = false;
                git = false;
                first = null;
            }

            if (all)
            {
                owner.hub.Publish(new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = Id, Path = string.Empty, Text = "all" });
            }

            foreach (string folder in changed)
            {
                owner.hub.Publish(new RemoteEvent { Kind = RemoteEventKind.FilesChanged, WorkspaceId = Id, Path = folder });
            }

            if (gitChanged)
            {
                owner.hub.Publish(new RemoteEvent { Kind = RemoteEventKind.GitChanged, WorkspaceId = Id });
            }
        }
    }
}
