namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Searches the workspace's files by name or by content on the server and lists the hits.
/// </summary>
internal sealed class FileSearchViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FileSearchViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace whose files are searched.</param>
    /// <param name="showHidden">Tells whether the files that a <c>.filehide</c> file hides are searched too.</param>
    public FileSearchViewModel(IServerConnection connection, Guid workspaceId, Func<bool> showHidden)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        this.showHidden = showHidden;
        ChooseCommand = new AsyncCommand(item =>
        {
            Choose((FileSearchItem)item!);
            return Task.CompletedTask;
        });
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly Func<bool> showHidden;

    private readonly TimeSpan debounce = TimeSpan.FromMilliseconds(200);

    private CancellationTokenSource? running;

    private bool isOpen;

    private bool searchContent;

    private string query = string.Empty;

    private bool searched;

    /// <summary>
    /// Raised with the hit and the text searched when a hit is chosen.
    /// </summary>
    public event Action<FileSearchResult, string>? Chosen;

    /// <summary>
    /// Gets the hits of the last search.
    /// </summary>
    public ObservableCollection<FileSearchItem> Results { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the search shows; closing it forgets the query and the hits.
    /// </summary>
    public bool IsOpen
    {
        get => isOpen;
        set
        {
            if (Set(ref isOpen, value))
            {
                Clear();
                query = string.Empty;
                Raise(nameof(Query));

                if (value)
                {
                    Run();
                }
            }
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the contents of the files are searched instead of their names.
    /// </summary>
    public bool SearchContent
    {
        get => searchContent;
        set
        {
            if (Set(ref searchContent, value))
            {
                Raise(nameof(Placeholder));
                Run();
            }
        }
    }

    /// <summary>
    /// Gets or sets the text to search for.
    /// </summary>
    public string Query
    {
        get => query;
        set
        {
            if (Set(ref query, value))
            {
                Run();
            }
        }
    }

    /// <summary>
    /// Gets the hint shown in the empty query box.
    /// </summary>
    public string Placeholder => searchContent ? "Search file contents" : "Search files by name";

    /// <summary>
    /// Gets a value indicating whether a search finished without any hit.
    /// </summary>
    public bool HasNoResults => searched && Results.Count == 0;

    /// <summary>
    /// Gets the command that chooses the hit passed as its parameter.
    /// </summary>
    public AsyncCommand ChooseCommand { get; }

    /// <summary>
    /// Chooses a hit.
    /// </summary>
    /// <param name="item">The hit.</param>
    public void Choose(FileSearchItem item) => Chosen?.Invoke(item.Result, query);

    private void Clear()
    {
        running?.Cancel();
        running = null;
        Results.Clear();
        searched = false;
        Raise(nameof(HasNoResults));
    }

    private void Run()
    {
        running?.Cancel();
        running = null;

        if (!isOpen || (searchContent && query.Length == 0))
        {
            Results.Clear();
            searched = false;
            Raise(nameof(HasNoResults));
            return;
        }

        CancellationTokenSource source = new();
        running = source;
        _ = Search(query, searchContent, source.Token);
    }

    private async Task Search(string text, bool content, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(debounce, cancellation);
            IReadOnlyList<FileSearchResult>? found = null;

            if (await Attempt(async () => found = await connection.SearchFiles(workspaceId, text, content, showHidden(), cancellation)) && !cancellation.IsCancellationRequested)
            {
                Results.Clear();

                foreach (FileSearchResult result in found!)
                {
                    Results.Add(new FileSearchItem(result));
                }

                searched = true;
                Raise(nameof(HasNoResults));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>
/// One hit of a file search as the list shows it.
/// </summary>
/// <param name="Result">The hit.</param>
internal sealed record FileSearchItem(FileSearchResult Result)
{
    /// <summary>
    /// Gets the first line: the matching line of a content hit, the file name of a name hit.
    /// </summary>
    public string Primary => Result.Line > 0 ? Result.Text : Result.Path[(Result.Path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Gets the second line: the file path with the line number of a content hit, the folder of a name hit.
    /// </summary>
    public string Secondary => Result.Line > 0 ? $"{Result.Path}:{Result.Line}" : Result.Path.LastIndexOf('/') is > 0 and int slash ? Result.Path[..slash] : string.Empty;
}
