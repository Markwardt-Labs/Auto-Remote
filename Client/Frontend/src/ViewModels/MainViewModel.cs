namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The app's root: decides which screen shows and owns the server connection.
/// </summary>
internal sealed class MainViewModel(IAppInfo info, IServerConnection.IFactory factory, ISettingsStore store, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null, IChime? chime = null, IReadyNotifier? notifier = null, IWorkKeeper? keeper = null, IVoiceRecorder? recorder = null) : ViewModelBase
{
    private IServerConnection? connection;

    private HomeViewModel? home;

    private Guid? requested;

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

    /// <summary>
    /// Brings a workspace to the front, as opening its notification does. Before the app is connected, which is when it was started by the notification, the request waits and is carried out as soon as the workspaces are shown.
    /// </summary>
    /// <param name="workspaceId">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is shown, or at once when the request is left waiting.</returns>
    public async Task Focus(Guid workspaceId)
    {
        if (home is { } shown && connection is not null)
        {
            Current = shown;
            await shown.Show(workspaceId);
        }
        else
        {
            requested = workspaceId;
        }
    }

    /// <summary>
    /// Replaces the connection with a fresh one after the app was away for a while, since a connection that was open then may have died without telling, and the screens read what they missed.
    /// </summary>
    /// <returns>A task that completes once the new connection is open or the server cannot be reached.</returns>
    public async Task Resume()
    {
        if (connection is not { } current)
        {
            return;
        }

        try
        {
            await current.Reconnect(CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException)
        {
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
        HomeViewModel opening = new(opened, layout, drafts, chime, null, notifier, keeper, recorder);
        home = opening;
        opening.ChangeServerRequested += async () =>
        {
            ConnectionSettings? saved = await store.Load(CancellationToken.None);
            await Close();
            ShowConnect(saved, null);
        };
        opening.NewWorkspaceRequested += async () =>
        {
            NewWorkspaceViewModel creating = new(opened);
            creating.Created += async workspace => await Open(opened, workspace.Id);
            creating.Cancelled += () => Current = opening;
            Current = creating;
            await creating.Open();
        };
        opening.CloneWorkspaceRequested += async () =>
        {
            CloneWorkspaceViewModel cloning = new(opened);
            cloning.Created += async workspace => await Open(opened, workspace.Id);
            cloning.Cancelled += () => Current = opening;
            Current = cloning;
            await cloning.Open();
        };
        Current = opening;
        Guid? target = requested ?? focus;
        requested = null;
        await opening.Start(target, false);
    }

    private async Task Close()
    {
        if (connection is { } old)
        {
            connection = null;
            home = null;
            await old.DisposeAsync();
        }
    }
}
