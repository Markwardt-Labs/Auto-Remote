namespace Markwardt.AutoRemote.Client;

/// <summary>
/// The first screen: asks for the server's host, port and access key.
/// </summary>
internal sealed class ConnectViewModel : ViewModelBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectViewModel" /> class.
    /// </summary>
    /// <param name="factory">Opens the connection.</param>
    /// <param name="initial">The settings to prefill, if any.</param>
    /// <param name="version">The product name and version to show.</param>
    /// <param name="locals">Finds the servers on this machine, or <see langword="null" /> when this client does not offer to connect to one.</param>
    /// <param name="rescan">How often to look for servers on this machine again, or <see langword="null" /> for every three seconds.</param>
    public ConnectViewModel(IServerConnection.IFactory factory, ConnectionSettings? initial, string version, ILocalServers? locals = null, TimeSpan? rescan = null)
    {
        this.factory = factory;
        this.locals = locals;
        this.rescan = rescan ?? TimeSpan.FromSeconds(3);
        this.initial = initial;
        Version = version;
        host = initial?.Host ?? string.Empty;
        port = initial?.Port.ToString() ?? "52679";
        accessKey = initial?.AccessKey ?? string.Empty;
        ConnectCommand = new AsyncCommand(_ => Connect());
    }

    private readonly IServerConnection.IFactory factory;

    private readonly ConnectionSettings? initial;

    private readonly ILocalServers? locals;

    private readonly TimeSpan rescan;

    private readonly CancellationTokenSource stopping = new();

    private string host;

    private string port;

    private string accessKey;

    /// <summary>
    /// Raised with the open connection and the settings that produced it.
    /// </summary>
    public event Action<IServerConnection, ConnectionSettings>? Connected;

    /// <summary>
    /// Gets the product name and version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets or sets the server host name or address.
    /// </summary>
    public string Host
    {
        get => host;
        set => Set(ref host, value);
    }

    /// <summary>
    /// Gets or sets the server TCP port as typed.
    /// </summary>
    public string Port
    {
        get => port;
        set => Set(ref port, value);
    }

    /// <summary>
    /// Gets or sets the access key.
    /// </summary>
    public string AccessKey
    {
        get => accessKey;
        set => Set(ref accessKey, value);
    }

    /// <summary>
    /// Gets the command that connects.
    /// </summary>
    public AsyncCommand ConnectCommand { get; }

    /// <summary>
    /// Gets the buttons that connect to a server on this machine, one for each that allows this account.
    /// </summary>
    public ObservableCollection<LocalServerChoice> LocalServers { get; } = [];

    /// <summary>
    /// Gets a value indicating whether a server on this machine can be connected to with one button.
    /// </summary>
    public bool HasLocalServers => LocalServers.Count > 0;

    /// <summary>
    /// Shows a failure, such as a failed automatic reconnect, on this screen.
    /// </summary>
    /// <param name="message">The failure message.</param>
    public void Fail(string message) => Error = message;

    /// <summary>
    /// Looks for servers on this machine now and then again every few seconds, so that one that starts later shows up, until <see cref="Stop" /> is called. Does nothing without a way to find them.
    /// </summary>
    /// <returns>A task that ends once <see cref="Stop" /> was called.</returns>
    public async Task Watch()
    {
        if (locals is null)
        {
            return;
        }

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                Show(await locals.Find(stopping.Token));
                await Task.Delay(rescan, stopping.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Stops looking for servers on this machine, because another screen replaced this one.
    /// </summary>
    public void Stop() => stopping.Cancel();

    private void Show(IReadOnlyList<LocalServer> servers)
    {
        if (servers.Select(server => server.Port).SequenceEqual(LocalServers.Select(choice => choice.Port)))
        {
            return;
        }

        LocalServers.Clear();

        foreach (LocalServer server in servers)
        {
            LocalServers.Add(new LocalServerChoice(server.Port, servers.Count == 1 ? "Connect to Local Server" : $"Connect to Local Server (Port {server.Port})", new AsyncCommand(_ => ConnectLocal(server.Port))));
        }

        Raise(nameof(HasLocalServers));
    }

    private async Task ConnectLocal(int serverPort)
    {
        ConnectionSettings settings = new() { Host = IPAddress.Loopback.ToString(), Port = serverPort, AccessKey = string.Empty, Local = true };
        IServerConnection? connection = null;

        if (await Attempt(async () => connection = await factory.Connect(settings, CancellationToken.None)) && connection is not null)
        {
            Connected?.Invoke(connection, settings);
        }
    }

    private async Task Connect()
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            Error = "Enter the server host.";
            return;
        }

        if (!int.TryParse(port, out int number) || number is < 1 or > 65535)
        {
            Error = "The port must be a number from 1 to 65535.";
            return;
        }

        if (string.IsNullOrEmpty(accessKey))
        {
            Error = "Enter the access key.";
            return;
        }

        ConnectionSettings settings = new() { Host = host.Trim(), Port = number, AccessKey = accessKey };
        IServerConnection? connection = null;

        if (await Attempt(async () => connection = await factory.Connect(settings, CancellationToken.None)) && connection is not null)
        {
            Connected?.Invoke(connection, settings);
        }
    }
}
