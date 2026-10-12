namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Browses the server machine's folders and creates new ones, to pick the folder that will host a workspace.
/// </summary>
/// <param name="connection">The server connection.</param>
internal sealed class FolderBrowserViewModel(IServerConnection connection) : ViewModelBase
{
    private FolderListing? listing;

    private FolderEntry? selected;

    private string newFolderName = string.Empty;

    /// <summary>
    /// Gets the sub-folders of the current folder.
    /// </summary>
    public ObservableCollection<FolderEntry> Folders { get; } = [];

    /// <summary>
    /// Gets the current folder's path, empty at the file system roots.
    /// </summary>
    public string CurrentPath => listing?.Path ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether a real folder (not the list of roots) is showing, so it can be chosen.
    /// </summary>
    public bool CanChoose => listing is { Path.Length: > 0 };

    /// <summary>
    /// Gets a value indicating whether there is a folder above the current one.
    /// </summary>
    public bool CanGoUp => listing?.Parent is not null;

    /// <summary>
    /// Gets or sets the tapped sub-folder; setting it opens that folder.
    /// </summary>
    public FolderEntry? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value) && value is not null)
            {
                _ = Open(value.Path);
            }
        }
    }

    /// <summary>
    /// Gets or sets the name typed for a new folder.
    /// </summary>
    public string NewFolderName
    {
        get => newFolderName;
        set => Set(ref newFolderName, value);
    }

    /// <summary>
    /// Gets the command that goes to the parent folder.
    /// </summary>
    public AsyncCommand UpCommand => field ??= new(_ => Open(listing?.Parent), () => CanGoUp);

    /// <summary>
    /// Gets the command that creates <see cref="NewFolderName" /> in the current folder and opens it.
    /// </summary>
    public AsyncCommand CreateFolderCommand => field ??= new(_ => CreateFolder());

    /// <summary>
    /// Shows the server's default folder.
    /// </summary>
    /// <returns>A task that completes once the folder is shown.</returns>
    public Task Start() => Open(null);

    private async Task Open(string? path)
    {
        FolderListing? result = null;

        if (await Attempt(async () => result = await connection.ListFolders(path, CancellationToken.None)))
        {
            Show(result!);
        }

        Selected = null;
    }

    private async Task CreateFolder()
    {
        FolderListing? result = null;

        if (string.IsNullOrWhiteSpace(newFolderName))
        {
            Error = "Enter a name for the new folder.";
            return;
        }

        if (!CanChoose)
        {
            Error = "Open a folder first.";
            return;
        }

        if (await Attempt(async () => result = await connection.CreateFolder(CurrentPath, newFolderName, CancellationToken.None)))
        {
            NewFolderName = string.Empty;
            Show(result!);
        }
    }

    private void Show(FolderListing value)
    {
        listing = value;
        Folders.Clear();

        foreach (FolderEntry folder in value.Folders)
        {
            Folders.Add(folder);
        }

        Raise(nameof(CurrentPath));
        Raise(nameof(CanChoose));
        Raise(nameof(CanGoUp));
        UpCommand.Refresh();
    }
}
