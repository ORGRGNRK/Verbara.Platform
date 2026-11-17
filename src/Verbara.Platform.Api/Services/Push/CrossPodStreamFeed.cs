using System.Reactive.Linq;
using System.Reactive.Subjects;
using Verbara.Platform.Core;

namespace Verbara.Platform.Api.Services.Push;

/// <summary>
/// The events another replica published that this replica's live streams deliver (agent-assist-works-end-to-end,
/// design D12): today the four <c>agentassist.*</c>, which the engine publishes on one replica while an agent's stream may
/// be served by another. Kept apart from <see cref="PlatformEventBus"/> on purpose: a record put on that bus would be
/// forwarded to the backplane again and dispatched to webhooks on every replica. Only the live stream reads it, through
/// the same delivery filter it applies to local events.
/// </summary>
public sealed class CrossPodStreamFeed : IDisposable
{
    private readonly Subject<PlatformEvent> _events = new();

    /// <summary>The received events, emitted synchronously on the receiver's thread, in the order it emits them.</summary>
    public IObservable<PlatformEvent> Events => _events.AsObservable();

    /// <summary>Whether a live stream is subscribed; the receiver decodes nothing while none is.</summary>
    public bool HasSubscribers => _events.HasObservers;

    /// <summary>Hands <paramref name="evt"/> to every subscribed stream.</summary>
    internal void Emit(PlatformEvent evt) => _events.OnNext(evt);

    /// <inheritdoc />
    public void Dispose() => _events.Dispose();
}
