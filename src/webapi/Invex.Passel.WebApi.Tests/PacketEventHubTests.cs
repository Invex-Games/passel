using Invex.Passel.WebApi.Domain.Aggregates;
using Invex.Passel.WebApi.Options;
using Invex.Passel.WebApi.Services;

namespace Invex.Passel.WebApi.Tests;

public sealed class PacketEventHubTests
{
    private static PacketEventHub CreateHub(int maxStreamsPerUser = 5, int maxStreamsPerInstance = 100) =>
        new(Microsoft.Extensions.Options.Options.Create(new EventStreamOptions
        {
            MaxStreamsPerUser = maxStreamsPerUser,
            MaxStreamsPerInstance = maxStreamsPerInstance,
        }));

    private static PacketEventSubscription Subscribe(PacketEventHub hub, string userId)
    {
        var subscription = hub.Subscribe(userId);
        Assert.NotNull(subscription);

        return subscription;
    }

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
        var hub = CreateHub();
        using var first = Subscribe(hub, "user");
        using var second = Subscribe(hub, "user");
        using var other = Subscribe(hub, "other");
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
        var hub = CreateHub();
        using var first = Subscribe(hub, "user");
        using var other = Subscribe(hub, "other");

        hub.PublishToAll(new PacketEvent.Resync());

        Assert.True(first.Reader.TryRead(out var firstEvent));
        Assert.IsType<PacketEvent.Resync>(firstEvent);
        Assert.True(other.Reader.TryRead(out var otherEvent));
        Assert.IsType<PacketEvent.Resync>(otherEvent);
    }

    [Fact]
    public void Disposing_a_subscription_unsubscribes_and_completes_it()
    {
        var hub = CreateHub();
        var subscription = Subscribe(hub, "user");
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
        var hub = CreateHub();
        using var subscription = Subscribe(hub, "user");

        for (var i = 0; i < PacketEventHub.SubscriberBufferSize + 1; i++)
            hub.Publish("user", Created("user"));

        // The buffered events can still be drained, then the reader completes so the client reconnects
        var drained = 0;

        while (subscription.Reader.TryRead(out _))
            drained++;

        Assert.Equal(PacketEventHub.SubscriberBufferSize, drained);
        Assert.True(subscription.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void A_user_over_their_cap_has_their_oldest_stream_closed()
    {
        var hub = CreateHub(maxStreamsPerUser: 2);
        using var first = Subscribe(hub, "user");
        using var second = Subscribe(hub, "user");

        using var third = Subscribe(hub, "user");

        Assert.True(first.Reader.Completion.IsCompleted);
        Assert.False(second.Reader.Completion.IsCompleted);

        hub.Publish("user", Created("user"));

        Assert.False(first.Reader.TryRead(out _));
        Assert.True(second.Reader.TryRead(out _));
        Assert.True(third.Reader.TryRead(out _));
    }

    [Fact]
    public void Disposing_a_closed_stream_does_not_close_another()
    {
        var hub = CreateHub(maxStreamsPerUser: 2);
        var first = Subscribe(hub, "user");
        using var second = Subscribe(hub, "user");
        using var third = Subscribe(hub, "user");
        Assert.True(first.Reader.Completion.IsCompleted);

        // The evicted stream ends and its client reconnects; neither step may count against the live streams
        first.Dispose();
        using var fourth = Subscribe(hub, "user");

        Assert.True(second.Reader.Completion.IsCompleted);
        Assert.False(third.Reader.Completion.IsCompleted);
        Assert.False(fourth.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void Subscribe_returns_null_when_the_instance_is_full()
    {
        var hub = CreateHub(maxStreamsPerInstance: 2);
        using var first = Subscribe(hub, "a");
        using var second = Subscribe(hub, "b");

        Assert.Null(hub.Subscribe("c"));
        Assert.False(hub.HasSubscribers("c"));
    }

    [Fact]
    public void A_stream_that_ends_makes_room_on_the_instance()
    {
        var hub = CreateHub(maxStreamsPerInstance: 1);
        var first = Subscribe(hub, "a");
        Assert.Null(hub.Subscribe("b"));

        first.Dispose();

        using var second = Subscribe(hub, "b");
        Assert.True(hub.HasSubscribers("b"));
    }

    [Fact]
    public void A_user_at_their_cap_can_still_reconnect_on_a_full_instance()
    {
        // Replacing a stream adds no load, so it is allowed when a new one would not be
        var hub = CreateHub(maxStreamsPerUser: 1, maxStreamsPerInstance: 1);
        using var first = Subscribe(hub, "user");

        using var second = Subscribe(hub, "user");

        Assert.True(first.Reader.Completion.IsCompleted);
        Assert.False(second.Reader.Completion.IsCompleted);
        Assert.Null(hub.Subscribe("other"));
    }
}
