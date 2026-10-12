namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A row of a tree of changed paths: a folder along the way, or a changed file at a leaf.
/// </summary>
internal sealed class ChangeNode : ViewModelBase
{
    private Action<ChangeNode>? open;

    private bool isExpanded = true;

    /// <summary>
    /// Gets the name of the folder or file.
    /// </summary>
    public string Name { get; private init; } = string.Empty;

    /// <summary>
    /// Gets the path relative to the workspace folder.
    /// </summary>
    public string Path { get; private init; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this is a folder.
    /// </summary>
    public bool IsFolder { get; private init; }

    /// <summary>
    /// Gets how the file changed, or <see langword="null" /> for a folder.
    /// </summary>
    public GitChangeStatus? Status { get; private init; }

    /// <summary>
    /// Gets the commit the change belongs to, or <see langword="null" /> for a change in the working tree.
    /// </summary>
    public string? Commit { get; private init; }

    /// <summary>
    /// Gets the nodes inside a folder, folders first and then files, each ordered by name.
    /// </summary>
    public List<ChangeNode> Children { get; } = [];

    /// <summary>
    /// Gets a value indicating whether the file is new.
    /// </summary>
    public bool IsAdded => Status == GitChangeStatus.Added;

    /// <summary>
    /// Gets a value indicating whether the file was changed.
    /// </summary>
    public bool IsModified => Status == GitChangeStatus.Modified;

    /// <summary>
    /// Gets a value indicating whether the file was removed.
    /// </summary>
    public bool IsDeleted => Status == GitChangeStatus.Deleted;

    /// <summary>
    /// Gets or sets a value indicating whether the folder is open.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }

    /// <summary>
    /// Gets the command that opens or closes a folder, or opens the diff of a file.
    /// </summary>
    public AsyncCommand OpenCommand => field ??= new(_ =>
    {
        if (IsFolder)
        {
            IsExpanded = !IsExpanded;
        }
        else
        {
            open?.Invoke(this);
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// Adds the changes to this node, which becomes the root of their tree.
    /// </summary>
    /// <param name="changes">The changed files.</param>
    /// <param name="commit">The commit the changes belong to, or <see langword="null" /> for the working tree.</param>
    /// <param name="opened">Called when a file of the tree is opened.</param>
    public void Fill(IEnumerable<GitChange> changes, string? commit, Action<ChangeNode> opened)
    {
        foreach (GitChange change in changes)
        {
            string[] parts = change.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            ChangeNode current = this;

            for (int i = 0; i < parts.Length; i++)
            {
                bool last = i == parts.Length - 1;
                ChangeNode? existing = current.Children.FirstOrDefault(child => child.Name == parts[i] && child.IsFolder == !last);

                if (existing is null)
                {
                    existing = new ChangeNode { Name = parts[i], Path = string.Join('/', parts[..(i + 1)]), IsFolder = !last, Status = last ? change.Status : null, Commit = last ? commit : null, open = opened };
                    current.Children.Add(existing);
                }

                current = existing;
            }
        }

        Sort();
    }

    /// <summary>
    /// Closes this folder and every folder in it.
    /// </summary>
    public void CollapseAll()
    {
        IsExpanded = false;

        foreach (ChangeNode child in Children)
        {
            child.CollapseAll();
        }
    }

    private void Sort()
    {
        Children.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        foreach (ChangeNode child in Children)
        {
            child.Sort();
        }
    }
}
