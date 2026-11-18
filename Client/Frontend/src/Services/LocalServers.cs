namespace Markwardt.AutoRemote.Client;

/// <summary>
/// Finds the servers on this machine that allow the account running the app and asks them, through their local channel, for what a connection needs.
/// </summary>
internal interface ILocalServers
{
    /// <summary>
    /// Lists the servers on this machine that answered; servers that do not allow this account are left out.
    /// </summary>
    /// <param name="cancellation">Cancels the search.</param>
    /// <returns>The servers, by ascending port.</returns>
    Task<IReadOnlyList<LocalServer>> Find(CancellationToken cancellation);

    /// <summary>
    /// Asks the server on a port for what a connection needs.
    /// </summary>
    /// <param name="port">The port of the server.</param>
    /// <param name="cancellation">Cancels the request.</param>
    /// <returns>What the server told.</returns>
    /// <exception cref="UnauthorizedAccessException">The server does not allow the account running the app.</exception>
    /// <exception cref="IOException">No server on that port is reachable through a local channel.</exception>
    /// <exception cref="InvalidDataException">The server's answer was not understood.</exception>
    Task<LocalHandover> Open(int port, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class LocalServers(ILocalEndpoint endpoint) : ILocalServers
{
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(3);

    private readonly string denied = "This account is not allowed to connect to the local server: it is not in the server's localUsers.";

    /// <inheritdoc />
    public async Task<IReadOnlyList<LocalServer>> Find(CancellationToken cancellation)
    {
        List<LocalServer> found = [];

        foreach (int port in Ports())
        {
            try
            {
                LocalHandover handover = await Open(port, cancellation);
                found.Add(new LocalServer { Port = handover.Port, Version = handover.Version });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<LocalHandover> Open(int port, CancellationToken cancellation)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);

        try
        {
            string text = OperatingSystem.IsWindows() ? await ReadPipe(port, limit.Token) : await ReadSocket(port, limit.Token);

            if (string.IsNullOrWhiteSpace(text))
            {
                throw new UnauthorizedAccessException(denied);
            }

            try
            {
                return JsonSerializer.Deserialize<LocalHandover>(text, options) ?? throw new InvalidDataException("The local server sent nothing.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"The local server sent something that is not understood: {exception.Message}", exception);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new IOException($"The local server on port {port} did not answer.");
        }
    }

    private IEnumerable<int> Ports()
    {
        SortedSet<int> ports = [];

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (string pipe in Directory.GetFiles(@"\\.\pipe\"))
                {
                    if (endpoint.Port(Path.GetFileName(pipe)) is { } port)
                    {
                        ports.Add(port);
                    }
                }
            }
            catch (IOException)
            {
            }
        }
        else
        {
            foreach (string folder in endpoint.Folders.Where(Directory.Exists))
            {
                foreach (string file in Directory.EnumerateFiles(folder))
                {
                    if (endpoint.Port(Path.GetFileName(file)) is { } port)
                    {
                        ports.Add(port);
                    }
                }
            }
        }

        return ports;
    }

    private async Task<string> ReadSocket(int port, CancellationToken cancellation)
    {
        bool refused = false;

        foreach (string folder in endpoint.Folders)
        {
            string path = endpoint.SocketPath(folder, port);

            if (!File.Exists(path))
            {
                continue;
            }

            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellation);
            }
            catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AccessDenied)
            {
                throw new UnauthorizedAccessException(denied, exception);
            }
            catch (SocketException)
            {
                refused = true;
                continue;
            }

            using NetworkStream stream = new(socket);
            using StreamReader reader = new(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellation);
        }

        throw new IOException(refused ? $"The local server on port {port} did not accept the connection." : $"No local server on port {port} was found.");
    }

    private async Task<string> ReadPipe(int port, CancellationToken cancellation)
    {
        using NamedPipeClientStream pipe = new(".", endpoint.PipeName(port), PipeDirection.In, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(cancellation);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException(denied, exception);
        }

        using StreamReader reader = new(pipe, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellation);
    }
}
