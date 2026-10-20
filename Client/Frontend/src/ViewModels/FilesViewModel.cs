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
    /// <param name="desktop">Whether this is the desktop layout: the tree always starts at the workspace folder, and keeps the open file selected with its folders open.</param>
    public FilesViewModel(IServerConnection connection, Guid workspaceId, GitViewModel git, bool desktop = false)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.desktop = desktop;
        Git = git;
        git.Updated += ApplyGit;
        git.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GitViewModel.IsChangesMode))
            {
                Raise(nameof(ShowTree));
                Raise(nameof(ShowChangeTree));
            }
        };
        Search = new FileSearchViewModel(connection, workspaceId, () => showHidden);
        Search.Chosen += (result, text) => _ = OpenResult(result, text);
        Search.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FileSearchViewModel.IsOpen))
            {
                Raise(nameof(ShowTree));
                Raise(nameof(ShowChangeTree));
            }
        };
        git.DiffRequested += (path, commit) => _ = OpenDiff(path, commit);
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

    private readonly bool desktop;

    private IReadOnlySet<string> running = new HashSet<string>();

    private WorkspaceFiles? files;

    private FileNode? selected;

    private FileContent? preview;

    private GitDiff? diff;

    private string diffTitle = string.Empty;

    private string? diffPath;

    private string markdownText = string.Empty;

    private bool restartFind;

    private string? diffCommit;

    private bool subscribed;

    private bool wrapLines;

    private bool markdownCollapsed;

    private bool showHidden;

    /// <summary>
    /// Raised with the path of a script or task file that was activated to run it.
    /// </summary>
    public event Action<string>? ScriptRequested;

    /// <summary>
    /// Gets the git state of the workspace.
    /// </summary>
    public GitViewModel Git { get; }

    /// <summary>
    /// Gets the search of the workspace's files by name or content.
    /// </summary>
    public FileSearchViewModel Search { get; }

    /// <summary>
    /// Gets a value indicating whether the file tree shows: outside the changes mode and the file search.
    /// </summary>
    public bool ShowTree => !Git.IsChangesMode && !Search.IsOpen;

    /// <summary>
    /// Gets a value indicating whether the tree of changed files shows: in the changes mode outside the file search.
    /// </summary>
    public bool ShowChangeTree => Git.IsChangesMode && !Search.IsOpen;

    /// <summary>
    /// Gets the search of the previewed file's text.
    /// </summary>
    public FileFindViewModel Find { get; } = new();

    /// <summary>
    /// Gets the entries of the root folder.
    /// </summary>
    public ObservableCollection<FileNode> Nodes { get; } = [];

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

                if (value is null)
                {
                    Find.IsOpen = false;
                }

                if (IsMarkdownPreview)
                {
                    restartFind = before != value?.Path;
                }
                else
                {
                    Find.Show(value?.Text ?? string.Empty, before != value?.Path);
                }

                Raise(nameof(IsFindable));
                Raise(nameof(IsPreviewing));
                Raise(nameof(IsMarkdownPreview));
                Raise(nameof(IsTextPreview));
                Raise(nameof(PreviewTitle));
                _ = Reveal();
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether long lines of the previewed text wrap instead of scrolling sideways.
    /// </summary>
    public bool WrapLines
    {
        get => wrapLines;
        set => Set(ref wrapLines, value);
    }

    /// <summary>
    /// Gets or sets a value indicating whether every heading of the rendered markdown is collapsed, which the markdown view keeps up to date as headings are folded.
    /// </summary>
    public bool MarkdownCollapsed
    {
        get => markdownCollapsed;
        set
        {
            if (Set(ref markdownCollapsed, value))
            {
                Raise(nameof(CollapseTip));
            }
        }
    }

    /// <summary>
    /// Gets the tooltip of the button that collapses or expands every heading of the rendered markdown.
    /// </summary>
    public string CollapseTip => markdownCollapsed ? "Expand all" : "Collapse all";

    /// <summary>
    /// Gets the tag shown for the previewed content: "Diff" for a diff, empty for a plain file.
    /// </summary>
    public string PreviewKind => diff is not null ? "Diff" : string.Empty;

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
                Raise(nameof(PreviewKind));
                Raise(nameof(PreviewTitle));
                _ = Reveal();
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
    /// Gets a value indicating whether the previewed file can be searched with the find bar.
    /// </summary>
    public bool IsFindable => preview is not null;

    /// <summary>
    /// Gets or sets the text of the rendered markdown preview as the view lays it out, which the find bar searches.
    /// </summary>
    public string MarkdownText
    {
        get => markdownText;
        set
        {
            markdownText = value;

            if (IsMarkdownPreview)
            {
                Find.Show(value, restartFind);
                restartFind = false;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the previewed file is shown as plain text.
    /// </summary>
    public bool IsTextPreview => preview is not null && !IsMarkdownPreview;

    /// <summary>
    /// Gets the previewed file's path, with a note when it was cut short.
    /// </summary>
    public string PreviewTitle => diff is not null ? diffTitle : preview is null ? string.Empty : preview.Truncated ? $"{preview.Path} (first part only)" : preview.Path;

    /// <summary>
    /// Gets a value indicating whether the tree has controls to move its root, which only the layout that is not desktop needs since the desktop tree always starts at the workspace folder.
    /// </summary>
    public bool ShowNavigation => !desktop;

    /// <summary>
    /// Gets or sets a value indicating whether the entries that a <c>.filehide</c> file hides show in the tree; changing it reloads the open folders.
    /// </summary>
    public bool ShowHidden
    {
        get => showHidden;
        set
        {
            if (Set(ref showHidden, value))
            {
                _ = Reload();
            }
        }
    }

    /// <summary>
    /// Gets or sets the selected node: the file being previewed with the desktop layout.
    /// </summary>
    public FileNode? Selected
    {
        get => selected;
        set => Set(ref selected, value);
    }

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
    /// Shows a failure the view hit while acting on the workspace, such as a folder that cannot be opened.
    /// </summary>
    /// <param name="message">The failure.</param>
    public void Fail(string message) => Error = message;

    /// <summary>
    /// Asks for a script or task file to be run, as double tapping it does; does nothing for any other node.
    /// </summary>
    /// <param name="node">The node.</param>
    public void Run(FileNode node)
    {
        if (node.IsRunnable)
        {
            ScriptRequested?.Invoke(node.Path);
        }
    }

    /// <summary>
    /// Marks the scripts and tasks that are running.
    /// </summary>
    /// <param name="paths">The paths of the running scripts and tasks.</param>
    public void ShowRunning(IReadOnlySet<string> paths)
    {
        running = paths;
        MarkRunning(Nodes);
    }

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

    private void MarkRunning(IEnumerable<FileNode> nodes)
    {
        foreach (FileNode node in nodes.Where(node => !node.IsPlaceholder))
        {
            node.IsScriptRunning = running.Contains(node.Path);
            MarkRunning(node.Children);
        }
    }

    private bool CanRun(FileEntry entry) => !entry.IsFolder && (entry.Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".task", StringComparison.OrdinalIgnoreCase));

    private void Apply(IEnumerable<FileNode> nodes)
    {
        foreach (FileNode node in nodes.Where(node => !node.IsPlaceholder))
        {
            node.GitStatus = Git.StatusOf(node.Path, node.IsFolder);
            Apply(node.Children);
        }
    }

    private string? OpenPath => preview?.Path ?? (diff is not null ? diffPath : null);

    private async Task Reveal()
    {
        if (!desktop)
        {
            return;
        }

        if (OpenPath is not { } path)
        {
            Selected = null;
            return;
        }

        string[] parts = path.Split('/');
        IList<FileNode> level = Nodes;
        string current = string.Empty;
        bool changed = false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            current = current.Length == 0 ? parts[i] : $"{current}/{parts[i]}";

            if (level.FirstOrDefault(node => node.IsFolder && node.Path == current) is not { } folder)
            {
                return;
            }

            if (!folder.IsLoaded)
            {
                await Load(folder);
            }

            if (!folder.IsExpanded)
            {
                changed = true;
                folder.IsExpanded = true;
            }

            level = folder.Children;
        }

        if (OpenPath == path)
        {
            Selected = level.FirstOrDefault(node => node.IsFile && node.Path == path);
        }

        if (changed)
        {
            await Watch();
        }
    }

    /// <summary>
    /// Opens or closes a folder, or previews a file, as tapping its node does.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>A task that completes once the file is shown.</returns>
    public async Task Choose(FileNode node)
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

        if (desktop)
        {
            await Reveal();
        }
        else
        {
            Selected = null;
        }
    }

    private async Task OpenResult(FileSearchResult result, string text)
    {
        FileContent? content = null;
        Search.IsOpen = false;

        if (await Attempt(async () => content = await connection.ReadFile(workspaceId, result.Path, CancellationToken.None)))
        {
            Preview = content;

            if (result.Line > 0)
            {
                Find.Query = text;
                Find.IsOpen = true;

                if (IsTextPreview)
                {
                    Find.Seek(result.Line);
                }
            }
        }
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

    private async Task Reload()
    {
        await Open(files?.Path ?? string.Empty, false);

        foreach (FileNode node in Nodes.Where(node => node.IsFolder && node.IsExpanded).ToList())
        {
            await Load(node);
        }

        await Watch();
    }

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
                target.Insert(i, new FileNode(entries[i], Expand) { GitStatus = Git.StatusOf(entries[i].Path, entries[i].IsFolder), IsRunnable = CanRun(entries[i]), IsScriptRunning = running.Contains(entries[i].Path) });
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

        if (await Attempt(async () => result = await connection.ListFiles(workspaceId, node.Path, showHidden, CancellationToken.None)))
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

        if (path is not null && await Attempt(async () => result = await connection.ListFiles(workspaceId, path, showHidden, CancellationToken.None)))
        {
            files = result;

            if (newRoot)
            {
                Nodes.Clear();
            }

            Sync(Nodes, result!.Entries);
            await Watch();
            _ = Reveal();
        }
    }
}
