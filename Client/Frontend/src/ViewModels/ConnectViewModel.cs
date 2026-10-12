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
    public ConnectViewModel(IServerConnection.IFactory factory, ConnectionSettings? initial, string version)
    {
        this.factory = factory;
        this.initial = initial;
        Version = version;
        host = initial?.Host ?? string.Empty;
        port = initial?.Port.ToString() ?? "52678";
        accessKey = initial?.AccessKey ?? string.Empty;
        ConnectCommand = new AsyncCommand(_ => Connect());
    }

    private readonly IServerConnection.IFactory factory;

    private readonly ConnectionSettings? initial;

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
    /// Shows a failure, such as a failed automatic reconnect, on this screen.
    /// </summary>
    /// <param name="message">The failure message.</param>
    public void Fail(string message) => Error = message;

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
