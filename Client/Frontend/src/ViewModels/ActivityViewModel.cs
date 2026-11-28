namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The activity of a workspace, the newest first: one entry per piece of work, followed live and read a page at a time.
/// </summary>
internal sealed class ActivityViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ActivityViewModel" /> class.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <param name="workspaceId">The workspace.</param>
    public ActivityViewModel(IServerConnection connection, Guid workspaceId)
    {
        this.connection = connection;
        this.workspaceId = workspaceId;
        ToggleCommand = new AsyncCommand(Toggle, null, false);
        LoadMoreCommand = new AsyncCommand(_ => LoadMore());
    }

    private readonly IServerConnection connection;

    private readonly Guid workspaceId;

    private readonly int pageSize = 50;

    // What is told while the newest page is read may be missing from it.
    private readonly List<ActivityEntry> early = [];

    private bool loading;

    private bool loadAgain;

    private bool loaded;

    private string? older;

    private int generation;

    /// <summary>
    /// Gets the entries, the newest first.
    /// </summary>
    public ObservableCollection<ActivityEntryViewModel> Entries { get; } = [];

    /// <summary>
    /// Gets a value indicating whether there is no activity, which the tab says in place of the entries.
    /// </summary>
    public bool IsEmpty => loaded && Entries.Count == 0;

    /// <summary>
    /// Gets a value indicating whether there are older entries to read.
    /// </summary>
    public bool HasMore => older is not null;

    /// <summary>
    /// Gets the command that shows or hides everything about the <see cref="ActivityEntryViewModel" /> given as its parameter.
    /// </summary>
    public AsyncCommand ToggleCommand { get; }

    /// <summary>
    /// Gets the command that reads older entries.
    /// </summary>
    public AsyncCommand LoadMoreCommand { get; }

    /// <summary>
    /// Reads the newest entries, in place of what is shown.
    /// </summary>
    /// <returns>A task that completes once they are shown or the failure to read them is.</returns>
    public async Task Load()
    {
        if (loading)
        {
            loadAgain = true;
            return;
        }

        loading = true;

        try
        {
            do
            {
                loadAgain = false;
                ActivityPage? page = null;

                if (await Attempt(async () => page = await connection.GetActivity(workspaceId, null, pageSize, CancellationToken.None)) && page is not null)
                {
                    Replace(page);
                }
            }
            while (loadAgain);
        }
        finally
        {
            loading = false;
            List<ActivityEntry> told = [.. early];
            early.Clear();

            foreach (ActivityEntry entry in told)
            {
                Apply(entry);
            }
        }
    }

    /// <summary>
    /// Follows an event of the server: an entry of this workspace that is new or changed; everything else is ignored.
    /// </summary>
    /// <param name="remoteEvent">The event.</param>
    public void Receive(RemoteEvent remoteEvent)
    {
        if (remoteEvent.WorkspaceId != workspaceId || remoteEvent.Kind != RemoteEventKind.ActivityChanged || remoteEvent.ActivityEntry is not { } entry)
        {
            return;
        }

        if (loading)
        {
            early.Add(entry);
        }
        else
        {
            Apply(entry);
        }
    }

    private void Replace(ActivityPage page)
    {
        HashSet<Guid> expanded = [.. Entries.Where(row => row.IsExpanded).Select(row => row.Entry.Id)];
        generation++;
        Entries.Clear();

        foreach (ActivityEntry entry in page.Entries)
        {
            Entries.Add(new ActivityEntryViewModel(entry, expanded.Contains(entry.Id)));
        }

        older = page.Older;
        loaded = true;
        RaiseList();
    }

    private async Task LoadMore()
    {
        if (older is not { } cursor)
        {
            return;
        }

        int read = generation;
        ActivityPage? page = null;

        // A page that was read for a list which was read anew meanwhile no longer belongs behind it.
        if (!await Attempt(async () => page = await connection.GetActivity(workspaceId, cursor, pageSize, CancellationToken.None)) || page is null || read != generation)
        {
            return;
        }

        foreach (ActivityEntry entry in page.Entries.Where(entry => Entries.All(row => row.Entry.Id != entry.Id)))
        {
            Entries.Add(new ActivityEntryViewModel(entry));
        }

        older = page.Older;
        RaiseList();
    }

    private void Apply(ActivityEntry entry)
    {
        for (int index = 0; index < Entries.Count; index++)
        {
            if (Entries[index].Entry.Id == entry.Id)
            {
                Entries[index] = Entries[index] with { Entry = entry };
                return;
            }
        }

        int place = 0;

        while (place < Entries.Count && Entries[place].Entry.StartedAt > entry.StartedAt)
        {
            place++;
        }

        // One older than everything read comes with its page.
        if (place == Entries.Count && older is not null)
        {
            return;
        }

        Entries.Insert(place, new ActivityEntryViewModel(entry));
        RaiseList();
    }

    private Task Toggle(object? parameter)
    {
        if (parameter is ActivityEntryViewModel row)
        {
            int index = Entries.IndexOf(row);

            if (index >= 0)
            {
                Entries[index] = row with { IsExpanded = !row.IsExpanded };
            }
        }

        return Task.CompletedTask;
    }

    private void RaiseList()
    {
        Raise(nameof(IsEmpty));
        Raise(nameof(HasMore));
    }
}
