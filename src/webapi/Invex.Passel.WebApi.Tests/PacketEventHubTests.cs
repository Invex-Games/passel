using Invex.Passel.WebApi.Domain.Aggregates;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class PacketEventHubTests
{
    private static PacketEvent.Created Created(string userId) =>
        new(new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Packet = new Packet.Text
            {
                Title = null,
                Body = "body",
            },
        });

    [Fact]
    public void Publish_reaches_only_the_users_subscriptions()
    {
        var hub = new PacketEventHub();
        using var first = hub.Subscribe("user");
        using var second = hub.Subscribe("user");
        using var other = hub.Subscribe("other");
        var packetEvent = Created("user");

        hub.Publish("user", packetEvent);

        Assert.True(first.Reader.TryRead(out var firstEvent));
        Assert.Same(packetEvent, firstEvent);
        Assert.True(second.Reader.TryRead(out var secondEvent));
        Assert.Same(packetEvent, secondEvent);
        Assert.False(other.Reader.TryRead(out _));
    }

    [Fact]
    public void PublishToAll_reaches_every_subscription()
    {
        var hub = new PacketEventHub();
        using var first = hub.Subscribe("user");
        using var other = hub.Subscribe("other");

        hub.PublishToAll(new PacketEvent.Resync());

        Assert.True(first.Reader.TryRead(out var firstEvent));
        Assert.IsType<PacketEvent.Resync>(firstEvent);
        Assert.True(other.Reader.TryRead(out var otherEvent));
        Assert.IsType<PacketEvent.Resync>(otherEvent);
    }

    [Fact]
    public void Disposing_a_subscription_unsubscribes_and_completes_it()
    {
        var hub = new PacketEventHub();
        var subscription = hub.Subscribe("user");
        Assert.True(hub.HasSubscribers("user"));

        subscription.Dispose();

        Assert.False(hub.HasSubscribers("user"));
        Assert.True(subscription.Reader.Completion.IsCompleted);

        // Publishing with no subscribers is a no-op
        hub.Publish("user", Created("user"));
    }

    [Fact]
    public void A_full_subscription_is_completed_instead_of_dropping_events()
    {
        var hub = new PacketEventHub();
        using var subscription = hub.Subscribe("user");

        for (var i = 0; i < PacketEventHub.SubscriberBufferSize + 1; i++)
            hub.Publish("user", Created("user"));

        // The buffered events can still be drained, then the reader completes so the client reconnects
        var drained = 0;

        while (subscription.Reader.TryRead(out _))
            drained++;

        Assert.Equal(PacketEventHub.SubscriberBufferSize, drained);
        Assert.True(subscription.Reader.Completion.IsCompleted);
    }
}
