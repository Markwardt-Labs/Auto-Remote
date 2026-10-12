namespace Markwardt.AutoRemote.Server;

/// <summary>
/// Fans events out to every connected client.
/// </summary>
internal interface IEventHub
{
    /// <summary>
    /// Starts delivering published events.
    /// </summary>
    /// <param name="handler">Called synchronously for each event, so it must not block.</param>
    /// <returns>Stops the delivery when disposed.</returns>
    IDisposable Subscribe(Action<RemoteEvent> handler);

    /// <summary>
    /// Delivers an event to every subscriber.
    /// </summary>
    /// <param name="remoteEvent">The event.</param>
    void Publish(RemoteEvent remoteEvent);
}

/// <inheritdoc />
internal sealed class EventHub : IEventHub
{
    private readonly ConcurrentDictionary<Guid, Action<RemoteEvent>> subscribers = new();

    /// <inheritdoc />
    public IDisposable Subscribe(Action<RemoteEvent> handler)
    {
        Guid key = Guid.NewGuid();
        subscribers[key] = handler;
        return new Subscription(() => subscribers.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public void Publish(RemoteEvent remoteEvent)
    {
        foreach (Action<RemoteEvent> handler in subscribers.Values)
        {
            handler(remoteEvent);
        }
    }

    private sealed class Subscription(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}
