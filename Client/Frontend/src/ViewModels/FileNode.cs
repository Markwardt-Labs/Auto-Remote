namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A file or folder in the file tree; a folder holds a placeholder child until its content is loaded, so the tree can show it as expandable without reading it.
/// </summary>
internal sealed class FileNode : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileNode" /> class for an entry.
    /// </summary>
    /// <param name="entry">The file or folder.</param>
    /// <param name="expanded">Called when the node is expanded or collapsed.</param>
    public FileNode(FileEntry entry, Action<FileNode> expanded)
    {
        Entry = entry;
        this.expanded = expanded;

        if (entry.IsFolder)
        {
            Children.Add(new FileNode());
        }
    }

    private FileNode()
    {
    }

    private readonly Action<FileNode>? expanded;

    private bool isExpanded;

    private GitChangeStatus? gitStatus;

    private bool isScriptRunning;

    /// <summary>
    /// Gets the file or folder, or <see langword="null" /> for a placeholder.
    /// </summary>
    public FileEntry? Entry { get; }

    /// <summary>
    /// Gets the nodes inside a folder.
    /// </summary>
    public ObservableCollection<FileNode> Children { get; } = [];

    /// <summary>
    /// Gets the name shown.
    /// </summary>
    public string Name => Entry?.Name ?? string.Empty;

    /// <summary>
    /// Gets the path relative to the workspace folder.
    /// </summary>
    public string Path => Entry?.Path ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether this is a folder.
    /// </summary>
    public bool IsFolder => Entry?.IsFolder == true;

    /// <summary>
    /// Gets a value indicating whether this is a file.
    /// </summary>
    public bool IsFile => Entry is { IsFolder: false };

    /// <summary>
    /// Gets a value indicating whether the file is a script or task that can be run.
    /// </summary>
    public bool IsRunnable { get; init; }

    /// <summary>
    /// Gets a value indicating whether the file is a runnable <c>.cs</c> script.
    /// </summary>
    public bool IsScriptFile => IsRunnable && Name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the file is a runnable <c>.task</c> file.
    /// </summary>
    public bool IsTaskFile => IsRunnable && Name.EndsWith(".task", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the file is neither a script nor a task shown as runnable.
    /// </summary>
    public bool IsPlainFile => IsFile && !IsRunnable;

    /// <summary>
    /// Gets or sets a value indicating whether the script or task is running.
    /// </summary>
    public bool IsScriptRunning
    {
        get => isScriptRunning;
        set => Set(ref isScriptRunning, value);
    }

    /// <summary>
    /// Gets a value indicating whether this only stands in for the content of a folder not read yet.
    /// </summary>
    public bool IsPlaceholder => Entry is null;

    /// <summary>
    /// Gets or sets how git sees the file or folder, or <see langword="null" /> when it is unchanged.
    /// </summary>
    public GitChangeStatus? GitStatus
    {
        get => gitStatus;
        set
        {
            if (Set(ref gitStatus, value))
            {
                Raise(nameof(IsAdded));
                Raise(nameof(IsModified));
                Raise(nameof(IsIgnored));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether git sees the file or folder as new.
    /// </summary>
    public bool IsAdded => gitStatus == GitChangeStatus.Added;

    /// <summary>
    /// Gets a value indicating whether git sees the file or folder as changed.
    /// </summary>
    public bool IsModified => gitStatus == GitChangeStatus.Modified;

    /// <summary>
    /// Gets a value indicating whether git ignores the file or folder.
    /// </summary>
    public bool IsIgnored => gitStatus == GitChangeStatus.Ignored;

    /// <summary>
    /// Gets or sets a value indicating whether the content of the folder has been read.
    /// </summary>
    public bool IsLoaded { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the folder is open.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value))
            {
                expanded?.Invoke(this);
            }
        }
    }
}
