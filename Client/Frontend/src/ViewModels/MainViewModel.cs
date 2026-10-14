namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The app's root: decides which screen shows and owns the server connection.
/// </summary>
internal sealed class MainViewModel(IAppInfo info, IServerConnection.IFactory factory, ISettingsStore store, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null, IChime? chime = null) : ViewModelBase
{
    private IServerConnection? connection;

    private object? current;

    /// <summary>
    /// Gets the view model of the screen showing now.
    /// </summary>
    public object? Current
    {
        get => current;
        private set
        {
            if (current is HomeViewModel previous)
            {
                previous.PropertyChanged -= HomeChanged;
            }

            if (Set(ref current, value))
            {
                if (value is HomeViewModel home)
                {
                    home.PropertyChanged += HomeChanged;
                }

                Raise(nameof(WindowTitle));
            }
        }
    }

    /// <summary>
    /// Gets the title for the window of a desktop client: the open workspace's name, or the app's name when none is open.
    /// </summary>
    public string WindowTitle => current is HomeViewModel home ? home.Title : "Auto Remote";

    /// <summary>
    /// Opens the app: reconnects to the saved server, or asks for one when none is saved or it cannot be reached.
    /// </summary>
    /// <returns>A task that completes once the first screen is showing.</returns>
    public async Task Start()
    {
        ConnectionSettings? saved = await store.Load(CancellationToken.None);

        if (saved is null)
        {
            ShowConnect(null, null);
            return;
        }

        try
        {
            await Open(await factory.Connect(saved, CancellationToken.None), null);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
        {
            ShowConnect(saved, exception.Message);
        }
    }

    private void HomeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.Title))
        {
            Raise(nameof(WindowTitle));
        }
    }

    private void ShowConnect(ConnectionSettings? settings, string? error)
    {
        ConnectViewModel connect = new(factory, settings, $"{info.Name} {info.Version}");
        connect.Connected += async (opened, used) =>
        {
            await store.Save(used, CancellationToken.None);
            await Open(opened, null);
        };

        if (error is not null)
        {
            connect.Fail(error);
        }

        Current = connect;
    }

    private async Task Open(IServerConnection opened, Guid? focus)
    {
        if (!ReferenceEquals(connection, opened))
        {
            await Close();
        }

        connection = opened;
        HomeViewModel home = new(opened, layout, drafts, chime);
        home.ChangeServerRequested += async () =>
        {
            ConnectionSettings? saved = await store.Load(CancellationToken.None);
            await Close();
            ShowConnect(saved, null);
        };
        home.NewWorkspaceRequested += async () =>
        {
            NewWorkspaceViewModel creating = new(opened);
            creating.Created += async workspace => await Open(opened, workspace.Id);
            creating.Cancelled += () => Current = home;
            Current = creating;
            await creating.Open();
        };
        home.CloneWorkspaceRequested += async () =>
        {
            CloneWorkspaceViewModel cloning = new(opened);
            cloning.Created += async workspace => await Open(opened, workspace.Id);
            cloning.Cancelled += () => Current = home;
            Current = cloning;
            await cloning.Open();
        };
        Current = home;
        await home.Start(focus, false);
    }

    private async Task Close()
    {
        if (connection is { } old)
        {
            connection = null;
            await old.DisposeAsync();
        }
    }
}
