namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Opens the local channel: a way for a client on the same machine to ask for the port and access key, answered only to the accounts <see cref="ServerConfig.LocalUsers" /> allows, as the operating system identifies them.
/// </summary>
internal interface ILocalListener
{
    /// <summary>
    /// Answers the allowed accounts that ask until cancelled. Does nothing when the config allows no account or this platform has no way to identify the account that asks, and ends quietly when the channel cannot be opened, because the server works without it.
    /// </summary>
    /// <param name="config">The server settings.</param>
    /// <param name="port">The port the server listens on.</param>
    /// <param name="fingerprint">The SHA-256 fingerprint of the certificate the server presents, in hexadecimal.</param>
    /// <param name="cancellation">Closes the channel.</param>
    /// <returns>A task that ends when the channel is closed.</returns>
    Task Run(ServerConfig config, int port, string fingerprint, CancellationToken cancellation);
}

/// <inheritdoc />
internal sealed class LocalListener(IAppInfo info, ILocalEndpoint endpoint, ILocalAccess.IFactory access, ILog log) : ILocalListener
{
    private readonly JsonSerializerOptions options = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task Run(ServerConfig config, int port, string fingerprint, CancellationToken cancellation)
    {
        if (config.LocalUsers is { Count: 0 })
        {
            return;
        }

        byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new LocalHandover { Host = IPAddress.Loopback.ToString(), Port = port, Key = config.Key, Version = info.Version, Fingerprint = fingerprint }, options) + "\n");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                await Pipe(config.LocalUsers, port, reply, cancellation);
            }
            else if (OperatingSystem.IsLinux())
            {
                await Sockets(access.Create(config.LocalUsers), port, reply, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or IdentityNotMappedException or ArgumentException)
        {
            log.Write($"The local channel could not be opened: {exception.Message}");
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task Sockets(ILocalAccess rule, int port, ReadOnlyMemory<byte> reply, CancellationToken cancellation)
    {
        List<Task> serving = [];
        List<string> paths = [];

        foreach (string folder in endpoint.Folders)
        {
            string path = endpoint.SocketPath(folder, port);

            try
            {
                Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.Delete(path);
                Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
                listener.Listen(8);
                paths.Add(path);
                serving.Add(Accept(listener, rule, reply, cancellation));
            }
            catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            await Task.WhenAll(serving);
        }
        finally
        {
            foreach (string path in paths)
            {
                File.Delete(path);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task Accept(Socket listener, ILocalAccess rule, ReadOnlyMemory<byte> reply, CancellationToken cancellation)
    {
        using (listener)
        {
            while (true)
            {
                Socket peer;

                try
                {
                    peer = await listener.AcceptAsync(cancellation);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                _ = Answer(peer, rule, reply, cancellation);
            }
        }
    }

    [SupportedOSPlatform("linux")]
    private async Task Answer(Socket peer, ILocalAccess rule, ReadOnlyMemory<byte> reply, CancellationToken cancellation)
    {
        using (peer)
        {
            try
            {
                byte[] credentials = new byte[12];
                peer.GetRawSocketOption(1, 17, credentials);

                if (rule.Allows(BitConverter.ToUInt32(credentials, 4)))
                {
                    using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    limit.CancelAfter(timeout);
                    await peer.SendAsync(reply, limit.Token);
                }

                peer.Shutdown(SocketShutdown.Both);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task Pipe(IReadOnlyList<string>? users, int port, ReadOnlyMemory<byte> reply, CancellationToken cancellation)
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        if (users is null)
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        else
        {
            foreach (string user in users)
            {
                security.AddAccessRule(new PipeAccessRule((SecurityIdentifier)new NTAccount(user.Trim()).Translate(typeof(SecurityIdentifier)), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }
        }

        NamedPipeServerStream pipe = CreatePipe(port, security);

        try
        {
            while (true)
            {
                await pipe.WaitForConnectionAsync(cancellation);
                NamedPipeServerStream next = CreatePipe(port, security);

                try
                {
                    using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    limit.CancelAfter(timeout);
                    await pipe.WriteAsync(reply, limit.Token);
                    await pipe.FlushAsync(limit.Token);
                    pipe.WaitForPipeDrain();
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException && !cancellation.IsCancellationRequested)
                {
                }
                finally
                {
                    await pipe.DisposeAsync();
                    pipe = next;
                }
            }
        }
        finally
        {
            await pipe.DisposeAsync();
        }
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreatePipe(int port, PipeSecurity security) => NamedPipeServerStreamAcl.Create(endpoint.PipeName(port), PipeDirection.Out, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
}
