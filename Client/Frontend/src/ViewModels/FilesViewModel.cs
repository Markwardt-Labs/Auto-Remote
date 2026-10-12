namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Browses and previews the files inside one workspace's folder as a tree of folders opened in place, following changes to the folders it shows as they happen.
/// </summary>
internal sealed class FilesViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FilesViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace whose folder is browsed.</param>
    /// <param name="git">The git state of the workspace, which colors the tree and supplies the diffs.</param>
    public FilesViewModel(IServerConnection connection, Guid workspaceId, GitViewModel git)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        Git = git;
        git.Updated += ApplyGit;
        git.DiffRequested += (path, commit) => _ = OpenDiff(path, commit);
        UpCommand = new AsyncCommand(_ => Open(files?.Parent), () => files?.Parent is not null);
        CollapseAllCommand = new AsyncCommand(_ => CollapseAll());
        ClosePreviewCommand = new AsyncCommand(_ =>
        {
            Preview = null;
            Diff = null;
            diffPath = null;
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly List<string> watched = [];

    private WorkspaceFiles? files;

    private FileNode? selected;

    private FileContent? preview;

    private GitDiff? diff;

    private string diffTitle = string.Empty;

    private string? diffPath;

    private string? diffCommit;

    private bool subscribed;

    /// <summary>
    /// Gets the git state of the workspace.
    /// </summary>
    public GitViewModel Git { get; }

    /// <summary>
    /// Gets the search of the previewed file's text.
    /// </summary>
    public FileFindViewModel Find { get; } = new();

    /// <summary>
    /// Gets the entries of the root folder.
    /// </summary>
    public ObservableCollection<FileNode> Nodes { get; } = [];

    /// <summary>
    /// Gets the root folder relative to the workspace folder, with a leading slash.
    /// </summary>
    public string CurrentPath => "/" + files?.Path;

    /// <summary>
    /// Gets the file being previewed, or <see langword="null" />.
    /// </summary>
    public FileContent? Preview
    {
        get => preview;
        private set
        {
            string? before = preview?.Path;

            if (Set(ref preview, value))
            {
                if (value is not null)
                {
                    Diff = null;
                }

                if (value is null || IsMarkdownPreview)
                {
                    Find.IsOpen = false;
                }

                Find.Show(value is null || IsMarkdownPreview ? string.Empty : value.Text, before != value?.Path);
                Raise(nameof(IsPreviewing));
                Raise(nameof(IsMarkdownPreview));
                Raise(nameof(IsTextPreview));
                Raise(nameof(PreviewTitle));
            }
        }
    }

    /// <summary>
    /// Gets the diff being previewed, or <see langword="null" />.
    /// </summary>
    public GitDiff? Diff
    {
        get => diff;
        private set
        {
            if (Set(ref diff, value))
            {
                Raise(nameof(IsDiffPreview));
                Raise(nameof(IsPreviewing));
                Raise(nameof(PreviewTitle));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether a diff is being previewed.
    /// </summary>
    public bool IsDiffPreview => diff is not null;

    /// <summary>
    /// Gets a value indicating whether a file or a diff is being previewed.
    /// </summary>
    public bool IsPreviewing => preview is not null || diff is not null;

    /// <summary>
    /// Gets a value indicating whether the previewed file is markdown and is shown rendered.
    /// </summary>
    public bool IsMarkdownPreview => preview is not null && (preview.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || preview.Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets a value indicating whether the previewed file is shown as plain text.
    /// </summary>
    public bool IsTextPreview => preview is not null && !IsMarkdownPreview;

    /// <summary>
    /// Gets the previewed file's path, with a note when it was cut short.
    /// </summary>
    public string PreviewTitle => diff is not null ? diffTitle : preview is null ? string.Empty : preview.Truncated ? $"{preview.Path} (first part only)" : preview.Path;

    /// <summary>
    /// Gets or sets the tapped node; setting it opens or closes the folder or previews the file.
    /// </summary>
    public FileNode? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value) && value is not null)
            {
                _ = Choose(value);
            }
        }
    }

    /// <summary>
    /// Gets the command that makes the parent of the root folder the root.
    /// </summary>
    public AsyncCommand UpCommand { get; }

    /// <summary>
    /// Gets the command that closes every open folder.
    /// </summary>
    public AsyncCommand CollapseAllCommand { get; }

    /// <summary>
    /// Gets the command that closes the file preview.
    /// </summary>
    public AsyncCommand ClosePreviewCommand { get; }

    /// <summary>
    /// Shows the root folder shown last, or the workspace folder at first, up to date, and asks the server to report its changes.
    /// </summary>
    /// <returns>A task that completes once the folder is shown.</returns>
    public Task Start()
    {
        if (!subscribed)
        {
            subscribed = true;
            connection.EventReceived += Receive;
        }

        return Open(files?.Path ?? string.Empty);
    }

    /// <summary>
    /// Makes a folder the root of the tree.
    /// </summary>
    /// <param name="node">The folder.</param>
    /// <returns>A task that completes once the folder is shown.</returns>
    public Task Enter(FileNode node) => node.IsFolder ? Open(node.Path) : Task.CompletedTask;

    /// <summary>
    /// Stops following changes.
    /// </summary>
    public void Dispose()
    {
        if (subscribed)
        {
            subscribed = false;
            connection.EventReceived -= Receive;
        }

        Git.Updated -= ApplyGit;
    }

    private async Task OpenDiff(string path, string? commit)
    {
        GitDiff? result = null;

        if (await Attempt(async () => result = await connection.GetGitDiff(workspaceId, path, commit, CancellationToken.None)))
        {
            Preview = null;
            diffPath = path;
            diffCommit = commit;
            diffTitle = commit is null ? $"{path} (changes)" : $"{path} ({commit[..Math.Min(7, commit.Length)]})";
            Diff = result;
            Raise(nameof(PreviewTitle));
        }
    }

    private void ApplyGit()
    {
        Apply(Nodes);

        if (diffPath is not null && diffCommit is null && diff is not null)
        {
            _ = OpenDiff(diffPath, null);
        }
    }

    private void Apply(IEnumerable<FileNode> nodes)
    {
        foreach (FileNode node in nodes.Where(node => !node.IsPlaceholder))
        {
            node.GitStatus = Git.StatusOf(node.Path, node.IsFolder);
            Apply(node.Children);
        }
    }

    private async Task Choose(FileNode node)
    {
        if (node.IsFolder)
        {
            node.IsExpanded = !node.IsExpanded;
        }
        else if (node.IsFile)
        {
            FileContent? content = null;

            if (await Attempt(async () => content = await connection.ReadFile(workspaceId, node.Path, CancellationToken.None)))
            {
                Preview = content;
            }
        }

        Selected = null;
    }

    private async Task CollapseAll()
    {
        foreach (ChangeNode node in Git.ChangeTree)
        {
            node.CollapseAll();
        }

        foreach (FileNode node in Nodes)
        {
            Collapse(node);
        }

        await Watch();
    }

    private void Collapse(FileNode node)
    {
        foreach (FileNode child in node.Children)
        {
            Collapse(child);
        }

        node.IsExpanded = false;
    }

    private void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.Kind == RemoteEventKind.FilesChanged && remoteEvent.WorkspaceId == workspaceId && remoteEvent.Path is { } path)
        {
            _ = Refresh(path);
        }
    }

    private FileNode? FindNode(IEnumerable<FileNode> nodes, string path)
    {
        foreach (FileNode node in nodes)
        {
            if (node.IsFolder && node.IsLoaded && node.IsExpanded)
            {
                if (node.Path == path)
                {
                    return node;
                }

                if (FindNode(node.Children, path) is { } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> Expanded(IEnumerable<FileNode> nodes) => nodes.Where(node => node.IsFolder && node.IsLoaded && node.IsExpanded).SelectMany(node => Expanded(node.Children).Prepend(node.Path));

    private async Task Refresh(string path)
    {
        if (path == files?.Path)
        {
            await Open(path, false);
        }
        else if (FindNode(Nodes, path) is { } node)
        {
            await Load(node);
        }

        if (preview is { } open && open.Path[..Math.Max(open.Path.LastIndexOf('/'), 0)] == path)
        {
            FileContent? content = null;
            Preview = await Attempt(async () => content = await connection.ReadFile(workspaceId, open.Path, CancellationToken.None)) ? content : null;
        }
    }

    private void Sync(ObservableCollection<FileNode> target, IReadOnlyList<FileEntry> entries)
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!entries.Any(entry => entry.Path == target[i].Path && entry.IsFolder == target[i].IsFolder))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            int at = target.ToList().FindIndex(node => node.Path == entries[i].Path);

            if (at < 0)
            {
                target.Insert(i, new FileNode(entries[i], Expand) { GitStatus = Git.StatusOf(entries[i].Path, entries[i].IsFolder) });
            }
            else if (at != i)
            {
                target.Move(at, i);
            }
        }
    }

    private async void Expand(FileNode node)
    {
        if (!node.IsExpanded)
        {
            node.IsLoaded = false;
        }
        else if (!node.IsLoaded)
        {
            await Load(node);
        }

        await Watch();
    }

    private async Task Load(FileNode node)
    {
        WorkspaceFiles? result = null;

        if (await Attempt(async () => result = await connection.ListFiles(workspaceId, node.Path, CancellationToken.None)))
        {
            Sync(node.Children, result!.Entries);
            node.IsLoaded = true;

            foreach (FileNode child in node.Children.Where(child => child.IsFolder && child.IsExpanded).ToList())
            {
                await Load(child);
            }
        }
        else
        {
            node.IsExpanded = false;
        }
    }

    private async Task Watch()
    {
        List<string> paths = Expanded(Nodes).Prepend(files?.Path ?? string.Empty).Distinct().ToList();

        if (!paths.SequenceEqual(watched) && await Attempt(() => connection.WatchFiles(workspaceId, paths, CancellationToken.None)))
        {
            watched.Clear();
            watched.AddRange(paths);
        }
    }

    private async Task Open(string? path, bool newRoot = true)
    {
        WorkspaceFiles? result = null;

        if (path is not null && await Attempt(async () => result = await connection.ListFiles(workspaceId, path, CancellationToken.None)))
        {
            files = result;

            if (newRoot)
            {
                Nodes.Clear();
            }

            Sync(Nodes, result!.Entries);
            Raise(nameof(CurrentPath));
            UpCommand.Refresh();
            await Watch();
        }
    }
}
