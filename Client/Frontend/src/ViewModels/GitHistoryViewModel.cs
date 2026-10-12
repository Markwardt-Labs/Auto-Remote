namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The commits of a workspace's repository, newest first, loaded in chunks.
/// </summary>
internal sealed class GitHistoryViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GitHistoryViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace.</param>
    /// <param name="git">The git state the history belongs to.</param>
    public GitHistoryViewModel(IServerConnection connection, Guid workspaceId, GitViewModel git)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.git = git;
        LoadMoreCommand = new AsyncCommand(_ => Load(Commits.Count, chunk, false), () => hasMore);
        RefreshCommand = new AsyncCommand(_ => Reload());
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly GitViewModel git;

    private readonly int chunk = 30;

    private bool hasMore;

    private bool loaded;

    /// <summary>
    /// Gets the git state the history belongs to, whose actions its branches and tags use.
    /// </summary>
    public GitViewModel Git => git;

    /// <summary>
    /// Gets the commits loaded so far, newest first.
    /// </summary>
    public ObservableCollection<GitCommitViewModel> Commits { get; } = [];

    /// <summary>
    /// Gets a value indicating whether older commits exist than the ones loaded.
    /// </summary>
    public bool HasMore => hasMore;

    /// <summary>
    /// Gets a value indicating whether the repository has no commits.
    /// </summary>
    public bool IsEmpty => loaded && Commits.Count == 0;

    /// <summary>
    /// Gets the command that loads older commits.
    /// </summary>
    public AsyncCommand LoadMoreCommand { get; }

    /// <summary>
    /// Gets the command that reads the history again.
    /// </summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>
    /// Shows the history, reading it the first time.
    /// </summary>
    /// <returns>A task that completes once the history is shown.</returns>
    public Task Start() => loaded ? Task.CompletedTask : Reload();

    /// <summary>
    /// Called when the head of the repository moved: reads the history again, but only once it was looked at.
    /// </summary>
    /// <returns>A task that completes once the history is up to date.</returns>
    public Task Changed() => loaded ? Reload() : Task.CompletedTask;

    private Task Reload() => Load(0, Math.Max(Commits.Count, chunk), true);

    private async Task Load(int skip, int limit, bool replace)
    {
        GitHistory? history = null;

        if (!await Attempt(async () => history = await connection.GetGitHistory(workspaceId, skip, limit, CancellationToken.None)))
        {
            return;
        }

        HashSet<string> open = [.. Commits.Where(commit => commit.IsExpanded).Select(commit => commit.Commit.Hash)];

        if (replace)
        {
            Commits.Clear();
        }

        foreach (GitCommit commit in history!.Commits)
        {
            Commits.Add(new GitCommitViewModel(connection, workspaceId, commit, node => git.RequestDiff(node.Path, node.Commit), git) { IsExpanded = open.Contains(commit.Hash) });
        }

        GitGraph graph = new([.. Commits.Select(commit => commit.Commit)]);

        for (int index = 0; index < Commits.Count; index++)
        {
            Commits[index].GraphLanes = graph.Lanes;
            Commits[index].Graph = graph.Rows[index];
        }

        hasMore = history.HasMore;
        loaded = true;
        Raise(nameof(HasMore));
        Raise(nameof(IsEmpty));
        LoadMoreCommand.Refresh();
    }
}
