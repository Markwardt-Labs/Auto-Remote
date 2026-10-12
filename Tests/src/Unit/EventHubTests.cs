namespace Markwardt.AutoRemote.Tests.Unit;

public sealed class EventHubTests
{
    private readonly EventHub hub = new();

    private readonly RemoteEvent remoteEvent = new() { Kind = RemoteEventKind.WorkingChanged, WorkspaceId = Guid.NewGuid(), Working = true };

    [Fact]
    public void Publish_ReachesEverySubscriberUntilItUnsubscribes()
    {
        List<RemoteEvent> first = [];
        List<RemoteEvent> second = [];
        IDisposable firstSubscription = hub.Subscribe(first.Add);
        hub.Subscribe(second.Add);

        hub.Publish(remoteEvent);
        firstSubscription.Dispose();
        hub.Publish(remoteEvent);

        Assert.Single(first);
        Assert.Equal(2, second.Count);
    }

    [Fact]
    public void Publish_WithoutSubscribersDoesNothing() => hub.Publish(remoteEvent);
}
