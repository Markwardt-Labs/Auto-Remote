namespace Markwardt.AutoRemote;

/// <summary>
/// Where the local channel of a server lives, so that the server and the clients on the same machine find each other: a named pipe on Windows and a Unix socket in a folder the server owns elsewhere.
/// </summary>
public interface ILocalEndpoint
{
    /// <summary>
    /// Gets the folders that can hold the sockets of servers on this machine, in the order a client looks; empty on Windows, which uses named pipes.
    /// </summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>
    /// Gets the name of the named pipe of the server on a port.
    /// </summary>
    /// <param name="port">The port of the server.</param>
    /// <returns>The pipe name, without the pipe namespace.</returns>
    string PipeName(int port);

    /// <summary>
    /// Gets the path of the socket of the server on a port in a folder.
    /// </summary>
    /// <param name="folder">One of <see cref="Folders" />.</param>
    /// <param name="port">The port of the server.</param>
    /// <returns>The socket path.</returns>
    string SocketPath(string folder, int port);

    /// <summary>
    /// Gets the port a socket or pipe name stands for.
    /// </summary>
    /// <param name="name">A file name from a folder in <see cref="Folders" />, or a pipe name.</param>
    /// <returns>The port, or <see langword="null" /> when the name is not one of a server's.</returns>
    int? Port(string name);
}

/// <inheritdoc />
public sealed class LocalEndpoint(Func<string, string?>? environment = null) : ILocalEndpoint
{
    private readonly Func<string, string?> read = environment ?? Environment.GetEnvironmentVariable;

    private readonly string prefix = "AutoRemote-";

    private readonly string suffix = ".sock";

    /// <inheritdoc />
    public IReadOnlyList<string> Folders
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return [];
            }

            List<string> folders = [];

            if (read("XDG_RUNTIME_DIR") is { Length: > 0 } runtime)
            {
                folders.Add(Path.Combine(runtime, "auto-remote"));
            }

            folders.Add("/run/auto-remote");
            return folders;
        }
    }

    /// <inheritdoc />
    public string PipeName(int port) => $"{prefix}{port}";

    /// <inheritdoc />
    public string SocketPath(string folder, int port) => Path.Combine(folder, $"{port}{suffix}");

    /// <inheritdoc />
    public int? Port(string name)
    {
        string trimmed = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : string.Empty;
        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65535 ? port : null;
    }
}
