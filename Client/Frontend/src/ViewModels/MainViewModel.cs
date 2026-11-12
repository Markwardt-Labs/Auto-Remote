namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The app's root: decides which screen shows and owns the server connection.
/// </summary>
internal sealed class MainViewModel(IAppInfo info, IServerConnection.IFactory factory, ISettingsStore store, ClientLayout layout = ClientLayout.Tabs, IDraftStore? drafts = null, IChime? chime = null, IReadyNotifier? notifier = null, IWorkKeeper? keeper = null, IVoiceRecorder? recorder = null, INewWindow? newWindow = null, IReleaseFeed? feed = null, IClientUpdater? updater = null, TimeSpan? retryInterval = null, TimeSpan? updateWait = null) : ViewModelBase
{
    private IServerConnection? connection;

    private HomeViewModel? home;

    private UpdateViewModel? update;

    private object? beforeUpdate;

    private readonly TimeSpan retryEvery = retryInterval ?? TimeSpan.FromSeconds(3);

    private readonly TimeSpan retryFor = TimeSpan.FromMinutes(2);

    private readonly TimeSpan updateWaitFor = updateWait ?? TimeSpan.FromMinutes(10);

    private int waitingRun;

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
    /// Opens the app: reconnects to the saved server, or asks for one when none is saved or it cannot be reached. A server that cannot be reached yet, such as one that is restarting after an update, is tried again in the background for a while, and the app opens by itself once it answers unless the user has begun to use the connect screen.
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

        DateTime? updateStart = await store.LoadUpdateStart(CancellationToken.None);

        try
        {
            IServerConnection opened = await factory.Connect(saved, CancellationToken.None);

            if (updateStart is not null)
            {
                await store.SaveUpdateStart(null, CancellationToken.None);
            }

            await Open(opened, null);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
        {
            if (updateStart is { } started && exception is not UnauthorizedAccessException && DateTime.UtcNow - started < updateWaitFor)
            {
                _ = WaitForUpdate(saved, exception.Message, started + updateWaitFor);
                return;
            }

            if (updateStart is not null)
            {
                await store.SaveUpdateStart(null, CancellationToken.None);
            }

            ConnectViewModel connect = ShowConnect(saved, exception.Message);

            if (exception is not UnauthorizedAccessException)
            {
                _ = Retry(connect, saved);
            }
        }
    }

    /// <summary>
    /// Brings a workspace to the front, as opening its notification does. Before the app is connected, which is when it was started by the notification, the request waits and is carried out as soon as the workspaces are shown.
    /// </summary>
    /// <param name="workspaceId">The workspace identifier.</param>
    /// <returns>A task that completes once the workspace is shown, or at once when the request is left waiting.</returns>
    public async Task Focus(Guid workspaceId)
    {
        if (home is { } shown && connection is not null && update is not { IsUpdating: true })
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

    private async Task WaitForUpdate(ConnectionSettings saved, string error, DateTime end)
    {
        UpdateWaitViewModel waiting = new();
        string last = error;
        waiting.Closed += async () => await GiveUp(waiting, saved, last);
        Current = waiting;

        while (ReferenceEquals(current, waiting) && DateTime.UtcNow < end)
        {
            await Task.Delay(retryEvery);

            if (!ReferenceEquals(current, waiting))
            {
                return;
            }

            IServerConnection opened;

            try
            {
                opened = await factory.Connect(saved, CancellationToken.None);
            }
            catch (UnauthorizedAccessException exception)
            {
                await GiveUp(waiting, saved, exception.Message, false);
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                last = exception.Message;
                continue;
            }

            if (ReferenceEquals(current, waiting))
            {
                await store.SaveUpdateStart(null, CancellationToken.None);
                await Open(opened, null);
            }
            else
            {
                await opened.DisposeAsync();
            }

            return;
        }

        await GiveUp(waiting, saved, last);
    }

    private async Task GiveUp(UpdateWaitViewModel waiting, ConnectionSettings saved, string error, bool retry = true)
    {
        if (!ReferenceEquals(current, waiting))
        {
            return;
        }

        await store.SaveUpdateStart(null, CancellationToken.None);
        ConnectViewModel connect = ShowConnect(saved, error);

        if (retry)
        {
            _ = Retry(connect, saved);
        }
    }

    private async Task WaitForServer(UpdateViewModel updating)
    {
        int run = ++waitingRun;
        await Task.Delay(updateWaitFor);

        if (run != waitingRun || !ReferenceEquals(update, updating) || updating.Phase != UpdatePhase.Waiting)
        {
            return;
        }

        ConnectionSettings? saved = await store.Load(CancellationToken.None);
        await store.SaveUpdateStart(null, CancellationToken.None);
        await Close();
        ConnectViewModel connect = ShowConnect(saved, "The server did not come back after the update.");

        if (saved is not null)
        {
            _ = Retry(connect, saved);
        }
    }

    private async Task Retry(ConnectViewModel connect, ConnectionSettings saved)
    {
        DateTime end = DateTime.UtcNow + retryFor;

        while (DateTime.UtcNow < end)
        {
            await Task.Delay(retryEvery);

            if (!ReferenceEquals(current, connect) || connect.IsBusy || connect.Host != saved.Host || connect.Port != saved.Port.ToString() || connect.AccessKey != saved.AccessKey)
            {
                return;
            }

            IServerConnection opened;

            try
            {
                opened = await factory.Connect(saved, CancellationToken.None);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                continue;
            }

            if (ReferenceEquals(current, connect) && !connect.IsBusy)
            {
                await Open(opened, null);
            }
            else
            {
                await opened.DisposeAsync();
            }

            return;
        }
    }

    private ConnectViewModel ShowConnect(ConnectionSettings? settings, string? error)
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
        return connect;
    }

    private async Task Open(IServerConnection opened, Guid? focus)
    {
        if (!ReferenceEquals(connection, opened))
        {
            await Close();
        }

        connection = opened;
        UpdateViewModel updating = update ??= Track(opened);

        if (IsMismatched(opened))
        {
            home = null;
            VersionMismatchViewModel mismatch = new(info, opened, updating);
            mismatch.ChangeServerRequested += () => _ = ChangeServer();
            Current = mismatch;
            return;
        }

        HomeViewModel opening = new(opened, layout, drafts, chime, null, notifier, keeper, recorder, newWindow, updating);
        home = opening;
        opening.ChangeServerRequested += () => _ = ChangeServer();
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
        opening.MoveWorkspaceRequested += async workspace =>
        {
            MoveWorkspaceViewModel moving = new(opened, workspace);
            moving.Moved += async moved => await Open(opened, moved.Id);
            moving.Cancelled += () => Current = opening;
            Current = moving;
            await moving.Open();
        };
        opening.EnvironmentRequested += async () =>
        {
            EnvironmentViewModel configuring = new(opened, update);
            configuring.Closed += () => Current = opening;
            Current = configuring;
            await configuring.Load();
        };
        Current = opening;
        Guid? target = requested ?? focus;
        requested = null;
        await opening.Start(target, false);
    }

    private UpdateViewModel Track(IServerConnection opened)
    {
        UpdateViewModel tracking = new(info, opened, feed, updater, null, store);
        tracking.ServerReconnected += () => _ = Recheck();
        tracking.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UpdateViewModel.Phase))
            {
                ShowUpdate(tracking);
            }
        };
        _ = tracking.Watch();
        return tracking;
    }

    private bool IsMismatched(IServerConnection opened) => opened.ServerVersion != info.Version;

    private async Task ChangeServer()
    {
        ConnectionSettings? saved = await store.Load(CancellationToken.None);
        await Close();
        ShowConnect(saved, null);
    }

    private void ShowUpdate(UpdateViewModel updating)
    {
        if (updating.Phase == UpdatePhase.Waiting)
        {
            _ = WaitForServer(updating);
        }

        if (updating.IsUpdating && current is not UpdateViewModel)
        {
            beforeUpdate = current;
            Current = updating;
        }
        else if (!updating.IsUpdating && current is UpdateViewModel)
        {
            Current = beforeUpdate;
            beforeUpdate = null;
        }
    }

    private async Task Recheck()
    {
        if (connection is not { } opened || update is not { IsInstalling: false })
        {
            return;
        }

        if (IsMismatched(opened))
        {
            if (current is HomeViewModel)
            {
                await Open(opened, null);
            }
        }
        else if (current is VersionMismatchViewModel)
        {
            await Open(opened, null);
        }
    }

    private async Task Close()
    {
        if (connection is { } old)
        {
            connection = null;
            home = null;
            beforeUpdate = null;
            update?.Dispose();
            update = null;
            await old.DisposeAsync();
        }
    }
}
