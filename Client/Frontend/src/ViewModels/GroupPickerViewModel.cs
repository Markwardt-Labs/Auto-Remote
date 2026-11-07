namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The choice of the group a new workspace or group is placed in: the top level or any group, each shown by its path from the top level.
/// </summary>
internal sealed class GroupPickerViewModel : ViewModelBase
{
    private GroupChoice selected = new(null, "Top level");

    /// <summary>
    /// Gets the places to choose from: the top level first, then every group in the order of the group tree.
    /// </summary>
    public ObservableCollection<GroupChoice> Choices { get; } = [new GroupChoice(null, "Top level")];

    /// <summary>
    /// Gets or sets the chosen place.
    /// </summary>
    public GroupChoice Selected
    {
        get => selected;
        set
        {
            if (value is not null && Set(ref selected, value))
            {
                Raise(nameof(GroupId));
            }
        }
    }

    /// <summary>
    /// Gets the chosen group, or <see langword="null" /> for the top level.
    /// </summary>
    public Guid? GroupId => selected.Id;

    /// <summary>
    /// Replaces the choices with the given groups, keeping the chosen place when it still exists and otherwise choosing the top level.
    /// </summary>
    /// <param name="groups">The groups.</param>
    public void Fill(IReadOnlyList<WorkspaceGroup> groups)
    {
        Guid? before = selected.Id;
        Choices.Clear();
        Choices.Add(new GroupChoice(null, "Top level"));
        Add(groups, null, string.Empty);
        Selected = Choices.FirstOrDefault(choice => choice.Id == before) ?? Choices[0];
    }

    /// <summary>
    /// Reads the groups from the server and fills the choices; a server that cannot be read leaves only the top level.
    /// </summary>
    /// <param name="connection">The server connection.</param>
    /// <returns>A task that completes once the choices are filled.</returns>
    public async Task Load(IServerConnection connection)
    {
        try
        {
            Fill((await connection.ListWorkspaces(CancellationToken.None)).Groups);
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or ObjectDisposedException or InvalidDataException)
        {
        }
    }

    private void Add(IReadOnlyList<WorkspaceGroup> groups, Guid? parent, string path)
    {
        foreach (WorkspaceGroup group in groups.Where(group => (groups.Any(other => other.Id == group.ParentId) ? group.ParentId : null) == parent))
        {
            string name = path.Length == 0 ? group.Name : $"{path} / {group.Name}";
            Choices.Add(new GroupChoice(group.Id, name));
            Add(groups, group.Id, name);
        }
    }
}
