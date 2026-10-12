namespace Markwardt.AutoRemote.Client;

/// <summary>
/// One commit in the history timeline; opening it lists the files it changed.
/// </summary>
internal sealed class GitCommitViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitCommitViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="commit">The commit.</param>
    /// <param name="opened">Called when a file the commit changed is opened.</param>
    /// <param name="git">The git state whose actions the commit's branches, tags and checkout use.</param>
    public GitCommitViewModel(IServerConnection connection, Guid workspaceId, GitCommit commit, Action<ChangeNode> opened, GitViewModel git)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.opened = opened;
        Commit = commit;
        string? current = commit.Refs.FirstOrDefault(item => item.IsCurrent)?.Name ?? git.CurrentBranch;
        Refs = [.. commit.Refs.Select(item => new GitRefViewModel(item, git, current))];
        ToggleCommand = new AsyncCommand(_ => Toggle());
        CheckoutCommand = new AsyncCommand(_ => git.Checkout(commit.Hash));
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly Action<ChangeNode> opened;

    private bool isExpanded;

    private bool isLoading;

    private bool loaded;

    /// <summary>
    /// Gets the commit.
    /// </summary>
    public GitCommit Commit { get; }

    /// <summary>
    /// Gets the branches and tags that point at the commit.
    /// </summary>
    public IReadOnlyList<GitRefViewModel> Refs { get; }

    /// <summary>
    /// Gets the tree of the files the commit changed, once it has been opened.
    /// </summary>
    public ObservableCollection<ChangeNode> Changes { get; } = [];

    /// <summary>
    /// Gets the subject of the commit.
    /// </summary>
    public string Subject => Commit.Subject;

    /// <summary>
    /// Gets the abbreviated hash.
    /// </summary>
    public string ShortHash => Commit.Hash[..Math.Min(7, Commit.Hash.Length)];

    /// <summary>
    /// Gets the author and the time of the commit.
    /// </summary>
    public string Detail => $"{Commit.Author} · {Commit.Date.LocalDateTime:MMM d, yyyy h:mm tt} · {ShortHash}";

    /// <summary>
    /// Gets a value indicating whether branches or tags point at the commit.
    /// </summary>
    public bool HasRefs => Refs.Count > 0;

    /// <summary>
    /// Gets or sets a value indicating whether the files of the commit are listed.
    /// </summary>
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (Set(ref isExpanded, value) && value && !loaded)
            {
                _ = Load();
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the files of the commit are being read.
    /// </summary>
    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (Set(ref isLoading, value))
            {
                Raise(nameof(HasNoChanges));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the commit is open and changed nothing.
    /// </summary>
    public bool HasNoChanges => loaded && !isLoading && Changes.Count == 0 && !HasError;

    /// <summary>
    /// Gets the command that opens or closes the commit.
    /// </summary>
    public AsyncCommand ToggleCommand { get; }

    /// <summary>
    /// Gets the command that checks the commit out.
    /// </summary>
    public AsyncCommand CheckoutCommand { get; }

    private Task Toggle()
    {
        IsExpanded = !IsExpanded;
        return Task.CompletedTask;
    }

    private async Task Load()
    {
        IsLoading = true;
        IReadOnlyList<GitChange> changes = [];

        if (await Attempt(async () => changes = await connection.GetGitCommitChanges(workspaceId, Commit.Hash, CancellationToken.None)))
        {
            ChangeNode root = new();
            root.Fill(changes, Commit.Hash, opened);
            Changes.Clear();

            foreach (ChangeNode node in root.Children)
            {
                Changes.Add(node);
            }

            loaded = true;
        }

        IsLoading = false;
    }
}
