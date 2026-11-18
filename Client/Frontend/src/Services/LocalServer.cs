namespace Markwardt.AutoRemote.Client;

/// <summary>
/// A server on this machine that allows the account running the app.
/// </summary>
internal sealed record LocalServer
{
    /// <summary>
    /// Gets the port the server listens on, which is also how it is told apart from another server on this machine.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// Gets the version of the server.
    /// </summary>
    public required string Version { get; init; }
}
