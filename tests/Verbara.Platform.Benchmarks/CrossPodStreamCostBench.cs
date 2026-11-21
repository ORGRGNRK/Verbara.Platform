// Verbara.Platform.Benchmarks — agent-assist-works-end-to-end task C.4, the M-cost gate of design D15 (frozen plan:
// Predictions/agent-assist-works-end-to-end.md, re-frozen after the BridgeEmission flip, sha256 65793dfe…, superseding
// aafc5c99…; verbara-meta/ADR-0030 decision 2). Reports the median beside the mean (D15 names the median).
//
// Drives the shipped code, never a copy: SseEndpoints.StreamSource / SubscribeDeliverable (the live stream's source and
// its delivery `.Where` + channel write), CrossPodStreamReceiver.TryAccept (one received envelope) and the bridge's
// bound emission path (AgentAssistBridge.Attach over AgentAssistSessionIdentity, already bound).
//
// Legs (per op):
//   LocalEvent        one agentassist.transcript, one conversation.message and one conversation.state_changed published
//                     on the bus, to `Streams` open streams. Divide by 3 × Streams for the per-event-and-stream figures.
//   ReceivedEnvelope  one envelope of each of the four agentassist.* types handed to the receiver. Divide by 4.
//   BridgeEmission    one of each of the four agentassist.* events of one session. Divide by 4.
//   BridgeFlush128    (information) a session whose tenant is unknown holds 128 transcripts, then binds and flushes
//                     them; includes the session's attach and detach. Divide by 128 (OperationsPerInvoke does).
//
// CROSSPOD_STREAM_MODE in the environment selects the variant, so A and B run the same binary in separate processes,
// interleaved A/B/A/B:
//   a  (A) LocalEvent: main's source (the bus alone). ReceivedEnvelope: envelopes of a non-agentassist type, dropped at
//          the type check. BridgeEmission: the four records published straight on the bus (the floor).
//   b  (B) LocalEvent: the candidate's merged source. ReceivedEnvelope: the four agentassist.* envelopes, decoded and
//          emitted to the streams. BridgeEmission: the candidate bridge's bound path.
//   n  (control) as b, plus: the receiver copies each payload into a new 2 KiB buffer; each stream's source adds a
//          `.Select` to a new 64-byte object per event; each bridge emission allocates a new 64-byte object inside the
//          identity's lock (in the bus's local subscriber, which Publish runs under that lock). The run must show them.
//
// Streams (`Streams` = 50): one named agent (the events' agent), two monitors, 47 other agents, one tenant; at 1, the
// named agent only. Each stream drains its channel once per op, as the write loop would.
//
// Repro: CROSSPOD_STREAM_MODE=b dotnet run -c Release --project tests/Verbara.Platform.Benchmarks -- --filter '*CrossPodStreamCostBench*'

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Endpoints.Shared;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.Push;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.AgentAssist.Domain;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Delivery;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Benchmarks;

[MemoryDiagnoser]
[MedianColumn]
[ShortRunJob]
public class CrossPodStreamCostBench
{
    private const string Tenant = "bench-tenant";
    private const string NamedAgent = "a-9e8d7c6b5a493827";
    private const string ConversationId = "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a";
    private const string SessionId = "as-5c4b3a29";
    private const string LinkedId = "1712345678.77";
    private const string AgentGuid = "11111111111111111111111111111111";
    private const int Held = 128;

    private static readonly string Text120 = new('x', 120);
    private static readonly JsonSerializerOptions BackplaneOptions = new() { TypeInfoResolver = PlatformPushJsonContext.Default };

    private readonly List<IDisposable> _disposables = [];
    private string _mode = "a";

    // LocalEvent / ReceivedEnvelope.
    private PlatformEventBus _bus = null!;
    private CrossPodStreamFeed _feed = null!;
    private CrossPodStreamReceiver _receiver = null!;
    private ChannelReader<PlatformEvent>[] _readers = [];
    private PlatformEvent[] _localEvents = [];
    private RemotePushEvent[] _envelopes = [];
    private object? _sink;

    // BridgeEmission / BridgeFlush128.
    private PlatformEventBus _bridgeBus = null!;
    private AgentAssistBridge _bridge = null!;
    private BenchSession _bound = null!;
    private long _recorded;
    private PlatformEvent? _last;

    [Params(1, 50)]
    public int Streams { get; set; }

    [GlobalSetup(Targets = new[] { nameof(LocalEvent) })]
    public void SetupLocalEvent()
    {
        ReadMode();
        _bus = Own(new PlatformEventBus());
        _feed = Own(new CrossPodStreamFeed());
        OpenStreams(_mode == "a" ? SseEndpoints.StreamSource(_bus, crossPodFeed: null) : SseEndpoints.StreamSource(_bus, _feed));
        _localEvents =
        [
            AgentAssist("agentassist.transcript"),
            new ConversationMessageEvent(Tenant, ConversationId, "m-1", "Inbound", "Voice")
                { Audience = new ConversationAudience(NamedAgent, null) },
            new ConversationStateChangedEvent(Tenant, ConversationId, "Active", "Resolved"),
        ];

        // The instrument checks itself: every stream gets the state change, the named agent and the monitors the content.
        var delivered = LocalEvent();
        var expected = Streams == 1 ? 3 : 3 * 3 + 47;
        if (delivered != expected)
            throw new InvalidOperationException($"LocalEvent delivered {delivered} frames, not {expected}.");
    }

    [GlobalSetup(Targets = new[] { nameof(ReceivedEnvelope) })]
    public void SetupReceivedEnvelope()
    {
        ReadMode();
        _bus = Own(new PlatformEventBus());
        _feed = Own(new CrossPodStreamFeed());
        var metrics = Own(new CrossPodStreamMetrics());
        _receiver = Own(new CrossPodStreamReceiver(new NoOpPushBus(), _feed, metrics, NullLogger<CrossPodStreamReceiver>.Instance));
        OpenStreams(SseEndpoints.StreamSource(_bus, _feed));

        string[] types = ["agentassist.suggestion", "agentassist.sentiment", "agentassist.compliance_alert", "agentassist.transcript"];
        _envelopes = [.. types.Select(t => EnvelopeOf(AgentAssist(t), _mode == "a" ? "conversation.message" : t))];

        var delivered = ReceivedEnvelope();
        var expected = _mode == "a" ? 0 : 4 * (Streams == 1 ? 1 : 3);
        if (delivered != expected)
            throw new InvalidOperationException($"ReceivedEnvelope delivered {delivered} frames, not {expected}.");
    }

    [GlobalSetup(Targets = new[] { nameof(BridgeEmission), nameof(BridgeFlush128) })]
    public void SetupBridge()
    {
        ReadMode();
        _bridgeBus = Own(new PlatformEventBus(new NoOpPushBus()));
        _disposables.Add(_bridgeBus.Events.Subscribe(e =>
        {
            _recorded++;
            _last = e;
            if (_mode == "n")
                _sink = new byte[40]; // 64 bytes on x64, allocated inside the identity's lock.
        }));

        var store = DispatchProxy.Create<IConversationStore, ConversationLookup>();
        ((ConversationLookup)(object)store).Answer = Task.FromResult<Conversation?>(new Conversation
        {
            ConversationId = EntityId.From(ConversationId), TenantId = new TenantId(Tenant), ContactId = EntityId.New(),
            Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch,
            VoiceLinkedId = LinkedId,
        });
        var supervisor = Own(new AgentAssistSupervisor());
        _bridge = Own(new AgentAssistBridge(supervisor, _bridgeBus, store, NullLogger<AgentAssistBridge>.Instance));

        // The bound session: its tenant known from the start, its conversation resolved before the first op. The lookup's
        // answer is an already-completed task, so Attach's first lookup resolves the conversation before it returns, and
        // the first emission is the causal signal: it is published bound, or the setup is wrong (no wait, no retry).
        _bound = Own(new BenchSession(SessionId, Call(Tenant)));
        _bridge.Attach(_bound.Feed);
        var before = _recorded;
        _bound.Say();
        if (_recorded != before + 1 || _last is not AgentAssistTranscriptEvent { ConversationId: ConversationId, TenantId: Tenant } t
            || t.AgentId != AgentGuid)
            throw new InvalidOperationException("The bench session's first emission was not published with its tenant, agent and conversation.");

        var published = _recorded;
        BridgeEmission();
        if (_recorded != published + 4)
            throw new InvalidOperationException($"BridgeEmission published {_recorded - published} events, not 4.");
        published = _recorded;
        BridgeFlush128();
        if (_recorded != published + Held + 1)
            throw new InvalidOperationException($"BridgeFlush128 published {_recorded - published} events, not {Held + 1}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
        _disposables.Clear();
        GC.KeepAlive(_sink);
    }

    [Benchmark]
    public int LocalEvent()
    {
        foreach (var evt in _localEvents)
            _bus.Publish(evt);
        return Drain();
    }

    [Benchmark]
    public int ReceivedEnvelope()
    {
        foreach (var envelope in _envelopes)
        {
            if (_mode == "n")
            {
                var copy = new byte[2048];
                envelope.RawPayload.CopyTo(copy, 0);
                _sink = copy;
            }

            _receiver.TryAccept(envelope);
        }

        return Drain();
    }

    [Benchmark]
    public long BridgeEmission()
    {
        if (_mode == "a")
        {
            // The floor: the same four records, built once each and published straight, no bridge and no identity.
            _bridgeBus.Publish(new AgentAssistSuggestionEvent(Tenant, SessionId, AgentGuid, ConversationId, "sg-01", Text120, "Important", "Keyword", null));
            _bridgeBus.Publish(new AgentAssistSentimentEvent(Tenant, SessionId, AgentGuid, ConversationId, "Caller", -0.62f, "Frustrated", BenchSession.TriggerWords));
            _bridgeBus.Publish(new AgentAssistComplianceAlertEvent(Tenant, SessionId, AgentGuid, ConversationId, "cr-identity-check", "full card number", "Warning"));
            _bridgeBus.Publish(new AgentAssistTranscriptEvent(Tenant, SessionId, AgentGuid, ConversationId, "Caller", Text120, IsFinal: true));
            return _recorded;
        }

        _bound.Suggest();
        _bound.Feel();
        _bound.Alert();
        _bound.Say();
        return _recorded;
    }

    [Benchmark(OperationsPerInvoke = Held)]
    public long BridgeFlush128()
    {
        // A session whose tenant is unknown holds every event; once the call carries the tenant, the next emission binds
        // the session (its conversation lookup completes synchronously here) and flushes the hold in order.
        using var session = new BenchSession("as-flush", Call(tenant: null));
        _bridge.Attach(session.Feed);
        for (var i = 0; i < Held; i++)
            session.Say();
        session.Call.TenantId = Tenant;
        session.Say();
        _bridge.Detach(session.SessionId);
        return _recorded;
    }

    private void ReadMode()
    {
        _mode = Environment.GetEnvironmentVariable("CROSSPOD_STREAM_MODE") ?? "a";
        if (_mode is not ("a" or "b" or "n"))
            throw new InvalidOperationException($"CROSSPOD_STREAM_MODE must be a, b or n, not {_mode}.");
        Console.WriteLine($"// CROSSPOD_STREAM_MODE={_mode}");
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private void OpenStreams(IObservable<PlatformEvent> source)
    {
        if (_mode == "n")
            source = source.Select(e => { _sink = new byte[40]; return e; }); // 64 bytes on x64 per event and stream.

        var filter = new PlatformDeliveryFilter();
        _readers = new ChannelReader<PlatformEvent>[Streams];
        for (var i = 0; i < Streams; i++)
        {
            var (userId, readsAll, agentId) = i switch
            {
                0 => ("u-named", false, NamedAgent),
                1 or 2 => ($"u-monitor-{i}", true, (string?)null),
                _ => ($"u-agent-{i}", false, $"a-other-{i}"),
            };
            var context = new SubscriberContext(Tenant, userId, new HashSet<string>(), new HashSet<string>());
            var channel = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
            _readers[i] = channel.Reader;
            _disposables.Add(SseEndpoints.SubscribeDeliverable(
                source, filter, new TenantId(Tenant), context, Reader(userId, readsAll, agentId), NullLogger.Instance, channel.Writer));
        }
    }

    private int Drain()
    {
        var frames = 0;
        foreach (var reader in _readers)
        {
            while (reader.TryRead(out _))
                frames++;
        }

        return frames;
    }

    private static PlatformEvent AgentAssist(string type) => type switch
    {
        "agentassist.suggestion" => new AgentAssistSuggestionEvent(Tenant, SessionId, NamedAgent, ConversationId, "sg-01",
            Text120, "Important", "Keyword", "card"),
        "agentassist.sentiment" => new AgentAssistSentimentEvent(Tenant, SessionId, NamedAgent, ConversationId, "Caller", -0.62f,
            "Frustrated", ["again", "third time"]),
        "agentassist.compliance_alert" => new AgentAssistComplianceAlertEvent(Tenant, SessionId, NamedAgent, ConversationId,
            "cr-identity-check", "full card number", "Warning"),
        "agentassist.transcript" => new AgentAssistTranscriptEvent(Tenant, SessionId, NamedAgent, ConversationId, "Caller", Text120,
            IsFinal: true),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // The envelope another replica's relay sends: the event's backplane payload and metadata, under `originalType`.
    private static RemotePushEvent EnvelopeOf(PlatformEvent evt, string originalType) =>
        new(originalType, "node-a", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, evt.GetType(), BackplaneOptions)))
        {
            Metadata = new PushEventMetadata(
                TenantId: evt.Metadata.TenantId,
                UserId: evt.Metadata.UserId,
                OccurredAt: evt.Metadata.OccurredAt,
                CorrelationId: evt.Metadata.CorrelationId,
                TopicPath: evt.Metadata.TopicPath,
                TraceContext: evt.Metadata.TraceContext),
        };

    // ConversationReaderClass and ConversationActor are built only from an HTTP request; the bench binds their private
    // constructors once per setup (JIT only, see the project file).
    private static ConversationReaderClass Reader(string userId, bool readsAll, string? agentId)
    {
        Agent? agent = agentId is null
            ? null
            : new Agent
            {
                AgentId = EntityId.From(agentId), TenantId = new TenantId(Tenant), UserId = EntityId.From(userId),
                DisplayName = userId, State = AgentState.Available, CreatedAt = DateTimeOffset.UnixEpoch,
            };
        var actor = (ConversationActor)Activator.CreateInstance(
            typeof(ConversationActor), BindingFlags.Instance | BindingFlags.NonPublic, null, [userId, agent], null)!;
        return (ConversationReaderClass)Activator.CreateInstance(
            typeof(ConversationReaderClass), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [actor, readsAll, agentId is not null], null)!;
    }

    private static CallSession Call(string? tenant) =>
        new("call-bench-1", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = tenant, AgentInterface = $"PJSIP/{Tenant}-agent-{AgentGuid}", QueueName = "support",
        };

    /// <summary>One announced session as the bridge reads it, with the fixture's example values pre-built.</summary>
    private sealed class BenchSession(string sessionId, CallSession call) : IDisposable
    {
        public static readonly string[] TriggerWords = ["again", "third time"];

        private static readonly TranscriptSegment Segment = new(Speaker.Caller, Text120, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        private static readonly AgentSuggestion Suggestion = new("sg-01", Text120, SuggestionPriority.Important, SuggestionSource.Keyword);
        private static readonly SentimentReading Reading = new(Speaker.Caller, -0.62f, SentimentLabel.Frustrated, TriggerWords, DateTimeOffset.UnixEpoch);
        private static readonly ComplianceAlert Alert_ = new("cr-identity-check", "full card number", ComplianceSeverity.Warning, DateTimeOffset.UnixEpoch);

        private readonly Subject<TranscriptSegment> _transcripts = new();
        private readonly Subject<AgentSuggestion> _suggestions = new();
        private readonly Subject<SentimentReading> _sentiment = new();
        private readonly Subject<ComplianceAlert> _compliance = new();

        public string SessionId { get; } = sessionId;
        public CallSession Call { get; } = call;

        public AgentAssistBridge.AssistSessionFeed Feed => new(
            SessionId, StartTenant: null, Call, _transcripts, _suggestions, _sentiment, _compliance);

        public void Say() => _transcripts.OnNext(Segment);
        public void Suggest() => _suggestions.OnNext(Suggestion);
        public void Feel() => _sentiment.OnNext(Reading);
        public void Alert() => _compliance.OnNext(Alert_);

        public void Dispose()
        {
            _transcripts.Dispose();
            _suggestions.Dispose();
            _sentiment.Dispose();
            _compliance.Dispose();
        }
    }

    /// <summary>A push bus that takes every publish and delivers nothing.</summary>
    private sealed class NoOpPushBus : IPushEventBus
    {
        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken cancellationToken = default)
            where TEvent : PushEvent => ValueTask.CompletedTask;

        public IObservable<PushEvent> AsObservable() => Observable.Never<PushEvent>();

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => Observable.Never<TEvent>();
    }
}

/// <summary>An <see cref="IConversationStore"/> that answers only the voice lookup the bound session makes.</summary>
public class ConversationLookup : DispatchProxy
{
    internal Task<Conversation?> Answer { get; set; } = Task.FromResult<Conversation?>(null);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        targetMethod?.Name == nameof(IConversationStore.FindByVoiceLinkedIdAsync)
            ? Answer
            : throw new NotSupportedException(targetMethod?.Name);
}
