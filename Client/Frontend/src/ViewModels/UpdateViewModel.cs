namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Looks on GitHub for a release newer than this client and its server and carries out the update the user confirms: the server first, which restarts, then the client.
/// </summary>
internal sealed class UpdateViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateViewModel" /> class.
    /// </summary>
    /// <param name="info">The version of this client.</param>
    /// <param name="connection">The connection to the server, whose version is compared and which is told to update.</param>
    /// <param name="feed">Finds the newest release, or <see langword="null" /> when the client does not look for one.</param>
    /// <param name="updater">Installs the release over this client, or <see langword="null" /> when this client cannot update itself and only updates its server.</param>
    /// <param name="interval">How often to look again, or <see langword="null" /> for every six hours.</param>
    public UpdateViewModel(IAppInfo info, IServerConnection connection, IReleaseFeed? feed, IClientUpdater? updater, TimeSpan? interval = null)
    {
        this.info = info;
        this.connection = connection;
        this.feed = feed;
        this.updater = updater;
        this.interval = interval ?? TimeSpan.FromHours(6);
        BeginCommand = new AsyncCommand(_ =>
        {
            Begin();
            return Task.CompletedTask;
        });
        ConfirmCommand = new AsyncCommand(_ => Run());
        CloseCommand = new AsyncCommand(_ =>
        {
            Close();
            return Task.CompletedTask;
        });
        connection.Reconnected += Reconnected;
    }

    private readonly IAppInfo info;

    private readonly IServerConnection connection;

    private readonly IReleaseFeed? feed;

    private readonly IClientUpdater? updater;

    private readonly TimeSpan interval;

    private readonly CancellationTokenSource stopping = new();

    private ReleaseInfo? latest;

    private UpdatePhase phase;

    private string status = string.Empty;

    private double progress;

    private string? checkError;

    /// <summary>
    /// Raised after the connection to the server was reopened, which is when the version of the server may have changed; the phase of the update has been settled by then.
    /// </summary>
    public event Action? ServerReconnected;

    /// <summary>
    /// Gets the command that asks for the confirmation of the update.
    /// </summary>
    public AsyncCommand BeginCommand { get; }

    /// <summary>
    /// Gets the command that carries out the update.
    /// </summary>
    public AsyncCommand ConfirmCommand { get; }

    /// <summary>
    /// Gets the command that closes the dialog.
    /// </summary>
    public AsyncCommand CloseCommand { get; }

    /// <summary>
    /// Gets how far the update has come.
    /// </summary>
    public UpdatePhase Phase
    {
        get => phase;
        private set
        {
            if (Set(ref phase, value))
            {
                Raise(nameof(IsOpen));
                Raise(nameof(IsUpdating));
                Raise(nameof(IsConfirming));
                Raise(nameof(IsWorking));
                Raise(nameof(IsInstalling));
                Raise(nameof(CanClose));
                Raise(nameof(CloseText));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the confirmation dialog is showing.
    /// </summary>
    public bool IsOpen => phase == UpdatePhase.Confirming;

    /// <summary>
    /// Gets a value indicating whether the updating screen is showing: from the confirmation on, the update runs on that screen and in the background of the app.
    /// </summary>
    public bool IsUpdating => phase is UpdatePhase.Working or UpdatePhase.Waiting or UpdatePhase.Finished;

    /// <summary>
    /// Gets a value indicating whether the confirmation is showing and the update is not running yet.
    /// </summary>
    public bool IsConfirming => phase == UpdatePhase.Confirming;

    /// <summary>
    /// Gets a value indicating whether the update is running or waiting for the server to restart.
    /// </summary>
    public bool IsWorking => phase is UpdatePhase.Working or UpdatePhase.Waiting;

    /// <summary>
    /// Gets a value indicating whether the update is running and cannot be interrupted.
    /// </summary>
    public bool IsInstalling => phase == UpdatePhase.Working;

    /// <summary>
    /// Gets a value indicating whether the updating screen may be left, which it may not while the update is running.
    /// </summary>
    public bool CanClose => phase != UpdatePhase.Working;

    /// <summary>
    /// Gets the label of the button that closes the confirmation or the updating screen.
    /// </summary>
    public string CloseText => phase == UpdatePhase.Confirming ? "Cancel" : "Close";

    /// <summary>
    /// Gets what the update is doing now.
    /// </summary>
    public string Status
    {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>
    /// Gets the fraction of the download that is done, from 0 to 1.
    /// </summary>
    public double Progress
    {
        get => progress;
        private set
        {
            if (Set(ref progress, value))
            {
                Raise(nameof(IsProgressUnknown));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether nothing was downloaded yet, so that the progress bar only shows that something is going on.
    /// </summary>
    public bool IsProgressUnknown => progress <= 0;

    /// <summary>
    /// Gets the newest release found on GitHub, or <see langword="null" /> when none was found yet.
    /// </summary>
    public ReleaseInfo? Latest => latest;

    /// <summary>
    /// Gets why the newest release could not be looked up, or <see langword="null" /> when it could.
    /// </summary>
    public string? CheckError
    {
        get => checkError;
        private set
        {
            if (Set(ref checkError, value))
            {
                Raise(nameof(HasCheckError));
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="CheckError" /> is set.
    /// </summary>
    public bool HasCheckError => checkError is not null;

    /// <summary>
    /// Gets a value indicating whether this client has a version older than the newest release and can install it.
    /// </summary>
    public bool ClientNeedsUpdate => updater is { IsSupported: true } && latest is not null && AppVersion.TryParse(info.Version, out AppVersion? own) && latest.Version > own;

    /// <summary>
    /// Gets a value indicating whether the server has a version older than the newest release, or does not say which it has.
    /// </summary>
    public bool ServerNeedsUpdate => latest is not null && (!AppVersion.TryParse(connection.ServerVersion, out AppVersion? server) || latest.Version > server);

    /// <summary>
    /// Gets a value indicating whether a newer release exists for the client or its server, which shows the update button.
    /// </summary>
    public bool IsAvailable => ClientNeedsUpdate || ServerNeedsUpdate;

    /// <summary>
    /// Gets what the update does, for the tooltip of the update button and for the confirmation.
    /// </summary>
    public string Summary => (ClientNeedsUpdate, ServerNeedsUpdate) switch
    {
        (true, true) => $"Version {latest!.Version} replaces {info.Version} on this device and {connection.ServerVersion ?? "the older version"} on the server. The server restarts, which stops the AI work it is doing, and then this app restarts.",
        (true, false) => $"Version {latest!.Version} replaces {info.Version} on this device. The app restarts.",
        (false, true) => $"Version {latest!.Version} replaces {connection.ServerVersion ?? "the older version"} on the server. The server restarts, which stops the AI work it is doing.",
        _ => "This client and its server are up to date.",
    };

    /// <summary>
    /// Looks up the newest release, then keeps looking at intervals until the view model is disposed.
    /// </summary>
    /// <returns>A task that ends when the view model is disposed.</returns>
    public async Task Watch()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                await Check();
                await Task.Delay(interval, stopping.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Looks up the newest release once.
    /// </summary>
    /// <returns>A task that completes when the answer is in.</returns>
    public async Task Check()
    {
        if (feed is null)
        {
            return;
        }

        try
        {
            latest = await feed.Latest(stopping.Token);
            CheckError = null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            CheckError = exception.Message;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        Refresh();
    }

    /// <summary>
    /// Shows the confirmation when an update is available.
    /// </summary>
    public void Begin()
    {
        if (phase == UpdatePhase.Idle && IsAvailable)
        {
            Error = null;
            Status = string.Empty;
            Progress = 0;
            Phase = UpdatePhase.Confirming;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        connection.Reconnected -= Reconnected;
        stopping.Cancel();
        stopping.Dispose();
    }

    private void Close()
    {
        if (CanClose)
        {
            Phase = UpdatePhase.Idle;
        }
    }

    private void Refresh()
    {
        Raise(nameof(Latest));
        Raise(nameof(ClientNeedsUpdate));
        Raise(nameof(ServerNeedsUpdate));
        Raise(nameof(IsAvailable));
        Raise(nameof(Summary));
    }

    private void Reconnected()
    {
        if (phase == UpdatePhase.Waiting)
        {
            Phase = UpdatePhase.Idle;
        }

        Refresh();
        ServerReconnected?.Invoke();
        _ = Check();
    }

    private async Task Run()
    {
        if (phase != UpdatePhase.Confirming || latest is not { } release)
        {
            return;
        }

        bool server = ServerNeedsUpdate;
        bool client = ClientNeedsUpdate;
        Error = null;
        Phase = UpdatePhase.Working;

        try
        {
            if (server)
            {
                Status = "Updating the server";

                try
                {
                    await connection.UpdateServer(CancellationToken.None);
                }
                catch (Exception exception) when (connection.ServerVersion is null && exception is IOException or RemoteException)
                {
                    throw new InvalidOperationException($"The server is too old to update itself ({exception.Message}). Install its newest release by hand.");
                }
            }

            if (client && updater is not null)
            {
                Status = $"Downloading version {release.Version}";
                await updater.Install(release, new Progress<double>(fraction => Progress = fraction), CancellationToken.None);
                Status = "The installer of the system finishes the update.";
                Phase = UpdatePhase.Finished;
            }
            else
            {
                Status = "The server restarts. This closes once it is back.";
                Phase = UpdatePhase.Waiting;
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or RemoteException or UnauthorizedAccessException or InvalidDataException or ObjectDisposedException or InvalidOperationException)
        {
            Error = exception.Message;
            Phase = UpdatePhase.Confirming;
        }
    }
}
