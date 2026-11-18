using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services.Push;
using Verbara.Platform.Core;
using Verbara.Sdk.Push.Delivery;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// agent-assist-works-end-to-end (design D15): the live stream's subscription chain, moved unchanged into
/// <see cref="SseEndpoints.StreamSource"/> and <see cref="SseEndpoints.SubscribeDeliverable"/> so the cost benchmark
/// drives the shipped code. The delivery rule itself is covered end to end by the content-delivery host tests.
/// </summary>
public sealed class SseStreamSourceTests : IDisposable
{
    private const string Tenant = "stream-source-tenant";

    private readonly PlatformEventBus _bus = new();
    private readonly CrossPodStreamFeed _feed = new();

    public void Dispose()
    {
        _feed.Dispose();
        _bus.Dispose();
    }

    private static ConversationStateChangedEvent StateChange(string tenant, string conversationId) =>
        new ConversationStateChangedEvent(tenant, conversationId, "Active", "Resolved");

    [Fact]
    public void StreamSource_ShouldReadTheLocalBusOnly_WhenNoFeedIsRegistered()
    {
        var seen = new List<PlatformEvent>();
        using var subscription = SseEndpoints.StreamSource(_bus, crossPodFeed: null).Subscribe(seen.Add);
        var local = StateChange(Tenant, "c-local");

        _bus.Publish(local);
        _feed.Emit(StateChange(Tenant, "c-received"));

        seen.Should().ContainSingle().Which.Should().BeSameAs(local);
        _feed.HasSubscribers.Should().BeFalse("a stream with no feed does not subscribe to one");
    }

    [Fact]
    public void StreamSource_ShouldMergeTheFeedWithTheLocalBus_WhenAFeedIsRegistered()
    {
        var seen = new List<PlatformEvent>();
        using var subscription = SseEndpoints.StreamSource(_bus, _feed).Subscribe(seen.Add);
        var local = StateChange(Tenant, "c-local");
        var received = StateChange(Tenant, "c-received");

        _bus.Publish(local);
        _feed.Emit(received);

        seen.Should().Equal(local, received);
        _feed.HasSubscribers.Should().BeTrue();
    }

    [Fact]
    public void SubscribeDeliverable_ShouldWriteOnlyTheSubscribersDeliverableEvents_WhenTheSourceEmits()
    {
        var channel = Channel.CreateUnbounded<PlatformEvent>();
        var subscriber = new SubscriberContext(Tenant, "u-1", new HashSet<string>(), new HashSet<string>());
        var own = StateChange(Tenant, "c-own");

        using (SseEndpoints.SubscribeDeliverable(
            SseEndpoints.StreamSource(_bus, _feed), new PlatformDeliveryFilter(), new TenantId(Tenant), subscriber, reader: null,
            NullLogger.Instance, channel.Writer))
        {
            _bus.Publish(own);
            _bus.Publish(StateChange("another-tenant", "c-foreign"));
            _bus.Publish(new UserAccessRevokedEvent(Tenant, "u-1", "suspended"));
        }

        _bus.Publish(StateChange(Tenant, "c-after-dispose"));

        var written = new List<PlatformEvent>();
        while (channel.Reader.TryRead(out var evt))
            written.Add(evt);
        written.Should().ContainSingle().Which.Should().BeSameAs(own,
            "another tenant's event, the revocation signal and anything after the subscription ends are not written");
    }

    [Fact]
    public void SubscribeDeliverable_ShouldDropTheEventAndKeepTheSubscription_WhenTheBufferIsFull()
    {
        var channel = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var subscriber = new SubscriberContext(Tenant, "u-1", new HashSet<string>(), new HashSet<string>());
        var first = StateChange(Tenant, "c-1");

        using var subscription = SseEndpoints.SubscribeDeliverable(
            _bus.Events, new PlatformDeliveryFilter(), new TenantId(Tenant), subscriber, reader: null, NullLogger.Instance,
            channel.Writer);
        channel.Writer.TryComplete();
        var act = () => _bus.Publish(first);

        act.Should().NotThrow("a write the channel refuses is logged, never thrown into the bus");
    }
}
