namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Takes the connections of paired servers, which the connection server hands over after the handshake. Such a connection gets no events and no access to anything but what it proves it may do with signed packages.
/// </summary>
internal interface IPeerListener
{
    /// <summary>
    /// Serves one connection until it ends.
    /// </summary>
    /// <param name="channel">The connection.</param>
    /// <param name="limit">Sets the longest message the connection takes from now on.</param>
    /// <param name="remote">Where it comes from.</param>
    /// <param name="cancellation">Ends it.</param>
    /// <returns>A task that completes once the connection ended.</returns>
    Task Serve(IMessageChannel channel, Action<int> limit, string remote, CancellationToken cancellation);
}
