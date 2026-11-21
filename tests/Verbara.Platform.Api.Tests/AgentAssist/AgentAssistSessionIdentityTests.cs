using System.Diagnostics.Metrics;
using System.Reactive.Subjects;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.AgentAssist.Domain;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Diagnostics;
using Verbara.Sdk.Push.Events;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D14): a session's identity (owner tenant, agent, conversation) is bound late and
/// re-evaluated, never frozen empty when the session starts. The bridge is driven through its session seam with
/// <see cref="Subject{T}"/> streams, a <see cref="FakeTimeProvider"/> and a substituted conversation store.
/// </summary>
public sealed class AgentAssistSessionIdentityTests : IDisposable
{
    private const string Demo = "demo";
    private const string Acme = "acme";
    private const string LinkedId = "1712345678.77";
    private const string DemoConversation = "c0000000000000000000000000000dem";
    private const string AcmeConversation = "c0000000000000000000000000000acm";
    private const string AgentGuid = "11111111111111111111111111111111";
    private const string FallbackAgent = "agent-fallback";

    private static readonly TimeSpan BindCheck = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ConversationHold = TimeSpan.FromSeconds(3);

    private readonly FakeTimeProvider _fake = new(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
    private readonly CountingTimeProvider _time;
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly AgentAssistHeldEventMetrics _metrics = new();
    private readonly HeldDrops _drops;
    private readonly RecordingLogger _logger = new();
    private readonly AgentAssistSupervisor _supervisor = new();
    private readonly List<IDisposable> _disposables = [];

    public AgentAssistSessionIdentityTests()
    {
        _time = new CountingTimeProvider(_fake);
        _drops = new HeldDrops(_metrics);
        ConversationFor(Demo, DemoConversation);
        ConversationFor(Acme, AcmeConversation);
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();
        _drops.Dispose();
        _metrics.Dispose();
        _supervisor.Dispose();
    }

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────────────────────────

    private void ConversationFor(string tenant, string? id) =>
        _conversations.FindByVoiceLinkedIdAsync(new TenantId(tenant), LinkedId, Arg.Any<CancellationToken>())
            .Returns(id is null ? Task.FromResult<Conversation?>(null) : Task.FromResult<Conversation?>(Tracked(tenant, id)));

    private void ConversationFor(string tenant, Task<Conversation?> lookup) =>
        _conversations.FindByVoiceLinkedIdAsync(new TenantId(tenant), LinkedId, Arg.Any<CancellationToken>())
            .Returns(lookup);

    private static Conversation Tracked(string tenant, string id) => new()
    {
        ConversationId = EntityId.From(id), TenantId = new TenantId(tenant), ContactId = EntityId.New(),
        Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch, VoiceLinkedId = LinkedId,
    };

    private static CallSession Call(string? tenant) =>
        new("call-identity-1", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = tenant, AgentId = FallbackAgent, AgentInterface = $"PJSIP/{Demo}-agent-{AgentGuid}", QueueName = "support",
        };

    private AgentAssistBridge Bridge(PlatformEventBus bus, IAgentAssistLiveSessionStore? store = null)
    {
        var bridge = new AgentAssistBridge(
            _supervisor, bus, _conversations, _logger, liveSessionStore: store, time: _time, heldMetrics: _metrics);
        _disposables.Add(bridge);
        return bridge;
    }

    private (PlatformEventBus Bus, Recorder Published) LocalBus()
    {
        var bus = new PlatformEventBus();
        _disposables.Add(bus);
        var published = new Recorder();
        _disposables.Add(bus.Events.Subscribe(e => published.Add(e)));
        return (bus, published);
    }

    private sealed class Session(string sessionId, string? startTenant, CallSession call)
    {
        public Subject<TranscriptSegment> Transcripts { get; } = new();
        public Subject<AgentSuggestion> Suggestions { get; } = new();
        public Subject<SentimentReading> Sentiment { get; } = new();
        public Subject<ComplianceAlert> Compliance { get; } = new();
        public CallSession Call { get; } = call;
        public string SessionId { get; } = sessionId;

        public AgentAssistBridge.AssistSessionFeed Feed => new(
            SessionId, startTenant, Call, Transcripts, Suggestions, Sentiment, Compliance);

        public void Say(string text) =>
            Transcripts.OnNext(new TranscriptSegment(Speaker.Caller, text, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));

        public void Feel(SentimentLabel label) =>
            Sentiment.OnNext(new SentimentReading(Speaker.Caller, -0.5f, label, ["late"], DateTimeOffset.UnixEpoch));

        public void Suggest(string text) =>
            Suggestions.OnNext(new AgentSuggestion("s-" + text, text, SuggestionPriority.Important, SuggestionSource.Keyword));

        public void Alert(string ruleId) =>
            Compliance.OnNext(new ComplianceAlert(ruleId, "phrase", ComplianceSeverity.Warning, DateTimeOffset.UnixEpoch));
    }

    private static Session Start(AgentAssistBridge bridge, string? startTenant, string? callTenant, string sessionId = "as-identity-1")
    {
        var session = new Session(sessionId, startTenant, Call(callTenant));
        bridge.Attach(session.Feed);
        return session;
    }

    private void Advance(TimeSpan by)
    {
        // Step by the bind check so every tick the identity schedules fires at its own time.
        var left = by;
        while (left > TimeSpan.Zero)
        {
            var step = left < BindCheck ? left : BindCheck;
            _fake.Advance(step);
            left -= step;
        }
    }

    private static string TextOf(PlatformEvent e) => e switch
    {
        AgentAssistTranscriptEvent t => t.Text,
        AgentAssistSuggestionEvent s => s.Text,
        AgentAssistSentimentEvent s => s.Label,
        AgentAssistComplianceAlertEvent c => c.RuleId,
        _ => e.Type,
    };

    private static (string Tenant, string Agent, string Conversation) IdentityOf(PlatformEvent e) => e switch
    {
        AgentAssistTranscriptEvent t => (t.TenantId, t.AgentId, t.ConversationId),
        AgentAssistSuggestionEvent s => (s.TenantId, s.AgentId, s.ConversationId),
        AgentAssistSentimentEvent s => (s.TenantId, s.AgentId, s.ConversationId),
        AgentAssistComplianceAlertEvent c => (c.TenantId, c.AgentId, c.ConversationId),
        _ => (e.TenantId, "", ""),
    };

    private static ConversationAudience AudienceOf(PlatformEvent e) => e switch
    {
        AgentAssistTranscriptEvent t => t.Audience,
        AgentAssistSuggestionEvent s => s.Audience,
        AgentAssistSentimentEvent s => s.Audience,
        AgentAssistComplianceAlertEvent c => c.Audience,
        _ => ConversationAudience.None,
    };

    // ─── Late stamp, late conversation ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Emit_ShouldPublishTheEarlierEventsInOrderWithTheStampedIdentity_WhenTheCallIsStampedAfterTheSessionStarted()
    {
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: null);

        session.Say("hello");
        session.Feel(SentimentLabel.Negative);
        session.Suggest("offer a refund");
        session.Alert("rule-1");

        published.Snapshot().Should().BeEmpty("nothing is published while the session's owner is unknown");

        session.Call.TenantId = Demo;
        session.Say("my order is late");

        var events = published.Snapshot();
        events.Select(TextOf).Should().Equal(["hello", "Negative", "offer a refund", "rule-1", "my order is late"]);
        events.Should().AllSatisfy(e => IdentityOf(e).Should().Be((Demo, AgentGuid, DemoConversation),
            "expected tenantId, the agent re-derived in it and its conversation"));
    }

    [Fact]
    public void Emit_ShouldHoldUpToTheConversationHoldThenPublishEmpty_WhenTheConversationRowLandsAfterTheStamp()
    {
        ConversationFor(Demo, (string?)null);
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: Demo, callTenant: Demo);

        session.Say("first");
        Advance(ConversationHold - TimeSpan.FromMilliseconds(250));
        published.Snapshot().Should().BeEmpty("an event is held while its conversation is unresolved, up to 3 s");

        Advance(TimeSpan.FromMilliseconds(250));
        published.Snapshot().Select(e => (TextOf(e), IdentityOf(e)))
            .Should().Equal([("first", (Demo, AgentGuid, ""))], "after 3 s it is published with an empty conversation");

        ConversationFor(Demo, DemoConversation);
        Advance(TimeSpan.FromSeconds(1));
        session.Say("second");

        published.Snapshot().Select(e => (TextOf(e), IdentityOf(e).Conversation))
            .Should().Equal([("first", ""), ("second", DemoConversation)], "a later event carries the conversation once it resolves");
    }

    [Fact]
    public void Detach_ShouldPublishNothingAndCountTenantUnresolved_WhenTheCallIsNeverStamped()
    {
        var (bus, published) = LocalBus();
        var bridge = Bridge(bus);
        var session = Start(bridge, startTenant: "", callTenant: null);

        session.Say("one");
        session.Say("two");
        session.Feel(SentimentLabel.Neutral);
        Advance(TimeSpan.FromSeconds(5));
        bridge.Detach(session.SessionId);

        published.Snapshot().Should().BeEmpty("a session that never had an owner publishes nothing");
        _drops.Of(AgentAssistHeldEventMetrics.TenantUnresolved).Should().Be(3);
    }

    [Fact]
    public async Task Emit_ShouldPublishNothingCountConflictAndRemoveTheListRecord_WhenTheCallTenantDiffersFromTheStartTenant()
    {
        ConversationFor(Demo, (string?)null);
        var store = new MemoryLiveSessionStore(_time);
        var (bus, published) = LocalBus();
        var bridge = Bridge(bus, store);
        var session = Start(bridge, startTenant: Demo, callTenant: null);

        session.Say("held for the conversation");
        (await store.ListAsync(Demo, CancellationToken.None)).Should().ContainSingle("the start-time tenant owns it at first");

        session.Call.TenantId = Acme;
        session.Say("after the conflict");
        Advance(TimeSpan.FromSeconds(6));
        session.Say("later");

        published.Snapshot().Should().BeEmpty("a conflicted session belongs to nobody");
        _drops.Of(AgentAssistHeldEventMetrics.Conflict).Should().Be(3);
        (await store.ListAsync(Demo, CancellationToken.None)).Should().BeEmpty("the list record is removed");
        (await store.ListAsync(Acme, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void Emit_ShouldDropTheOldestAndCountOverflow_WhenTheHoldPassesItsCapacity()
    {
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: null);

        for (var i = 0; i < 129; i++)
            session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        session.Call.TenantId = Demo;
        Advance(BindCheck);

        published.Snapshot().Select(TextOf).Should().Equal(
            Enumerable.Range(1, 128).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "the hold keeps the latest 128 in order");
        _drops.Of(AgentAssistHeldEventMetrics.Overflow).Should().Be(1);
    }

    [Fact]
    public void Emit_ShouldPublishDirectly_WhenTheSessionIsBound()
    {
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: Demo, callTenant: Demo);

        session.Say("now");

        published.Snapshot().Select(e => (TextOf(e), IdentityOf(e)))
            .Should().Equal([("now", (Demo, AgentGuid, DemoConversation))], "a bound session's event is not held");
        _time.LiveTimers.Should().Be(0, "nothing is held, so no bind check runs");
    }

    [Fact]
    public void Detach_ShouldPublishNothingCountNothingAndLeaveNoTimer_WhenTheSessionEndsRightAfterItStarted()
    {
        var (bus, published) = LocalBus();
        var bridge = Bridge(bus);
        var session = Start(bridge, startTenant: "", callTenant: null);

        bridge.Detach(session.SessionId);
        Advance(TimeSpan.FromSeconds(1));

        published.Snapshot().Should().BeEmpty();
        _drops.Total.Should().Be(0, "nothing was held");
        _time.LiveTimers.Should().Be(0, "the session's bind check is stopped");
    }

    [Fact]
    public void Emit_ShouldHoldAndLaterPublishTheFirstTranscript_WhenTheConversationLookupNeverCompletes()
    {
        ConversationFor(Demo, new TaskCompletionSource<Conversation?>().Task);
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: null);

        // Emitted right after the announcement returns: the bridge subscribed before any await.
        session.Say("I am calling about my bill");
        session.Call.TenantId = Demo;
        Advance(BindCheck);
        published.Snapshot().Should().BeEmpty("held while the conversation is looked up");

        Advance(ConversationHold);

        published.Snapshot().Select(e => (TextOf(e), IdentityOf(e)))
            .Should().Equal([("I am calling about my bill", (Demo, AgentGuid, ""))], "it is never lost");
    }

    [Fact]
    public void Emit_ShouldStampTheEmissionTimeAndTheReDerivedAudience_WhenHeldEventsAreFlushed()
    {
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: null);
        var emittedAt = new List<DateTimeOffset>();

        for (var i = 0; i < 3; i++)
        {
            emittedAt.Add(_fake.GetUtcNow());
            session.Say("line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _fake.Advance(TimeSpan.FromMilliseconds(100));
        }

        _fake.Advance(TimeSpan.FromMilliseconds(200));
        session.Call.TenantId = Demo;
        Advance(BindCheck);
        emittedAt.Add(_fake.GetUtcNow());
        session.Feel(SentimentLabel.Positive);

        var events = published.Snapshot();
        events.Select(e => e.Timestamp).Should().Equal(emittedAt, "each event keeps the time it was emitted, not the flush time");
        events.Select(e => e.Metadata.OccurredAt).Should().Equal(emittedAt);
        events.Select(e => e.Timestamp).Should().BeInAscendingOrder();
        events.Should().AllSatisfy(e => AudienceOf(e).Should().Be(ConversationAudience.ForAgent(AgentGuid),
            "the record is built with the bound agent"));
    }

    // ─── An owner change with no start-time tenant ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Emit_ShouldNeverPairTheNewOwnerWithTheFormerOwnersConversation_WhenTheCallMovesFromDemoToAcmeAfterDemosConversationResolved()
    {
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: Demo);

        session.Say("under demo");
        published.Snapshot().Select(e => IdentityOf(e).Conversation).Should().Equal([DemoConversation]);

        session.Call.TenantId = Acme;
        session.Say("under acme");
        session.Feel(SentimentLabel.Neutral);
        Advance(ConversationHold);

        var events = published.Snapshot();
        events.Where(e => e.TenantId == Acme && IdentityOf(e).Conversation == DemoConversation)
            .Should().BeEmpty("an acme event never carries demo's conversation");
        events.Skip(1).Should().HaveCount(2).And.AllSatisfy(e =>
            IdentityOf(e).Should().Be((Acme, FallbackAgent, AcmeConversation), "expected tenantId acme with acme's conversation"));
    }

    [Fact]
    public async Task Emit_ShouldDiscardTheFormerOwnersLookup_WhenTheCallMovesFromDemoToAcmeWhileDemosLookupIsPending()
    {
        var demoLookup = new TaskCompletionSource<Conversation?>();
        ConversationFor(Demo, demoLookup.Task);
        ConversationFor(Acme, (string?)null);
        var (bus, published) = LocalBus();
        var session = Start(Bridge(bus), startTenant: "", callTenant: Demo);

        session.Say("under demo");
        session.Call.TenantId = Acme;
        session.Say("under acme");
        // Completed on a thread with no synchronization context, so the lookup's continuation runs inline and has returned
        // (while acme owns the call) before the next line.
        await Task.Run(() => demoLookup.SetResult(Tracked(Demo, DemoConversation))).WaitAsync(TimeSpan.FromSeconds(10));
        session.Say("after demo's lookup returned");
        Advance(ConversationHold + BindCheck);

        var events = published.Snapshot();
        events.Where(e => e.TenantId == Acme && IdentityOf(e).Conversation == DemoConversation)
            .Should().BeEmpty("a lookup started under demo never lands on an acme event");
        events.Select(TextOf).Should().Equal(["under demo", "under acme", "after demo's lookup returned"]);
        events.Should().AllSatisfy(e => IdentityOf(e).Should().Be((Acme, FallbackAgent, ""), "expected tenantId acme"));
    }

    // ─── Concurrency ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Emit_ShouldKeepEmissionOrder_WhenEventsAreEmittedWhileTheHoldIsFlushed()
    {
        var (bus, published) = LocalBus();
        var bridge = Bridge(bus);

        for (var round = 0; round < 200; round++)
        {
            var sessionId = "as-flush-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var session = Start(bridge, startTenant: "", callTenant: null, sessionId);
            for (var i = 0; i < 64; i++)
                session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            session.Call.TenantId = Demo;

            // The emitter starts once the bind check has published the first held event, so its events race the rest of
            // the flush.
            using var flushing = new ManualResetEventSlim();
            using var started = bus.Events.Subscribe(e =>
            {
                if (e is AgentAssistTranscriptEvent { Text: "0" } t && t.SessionId == sessionId)
                    flushing.Set();
            });
            var flusher = Task.Factory.StartNew(
                () => _fake.Advance(BindCheck), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var emitter = Task.Factory.StartNew(() =>
            {
                flushing.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the bind check flushes the hold");
                for (var i = 64; i < 96; i++)
                    session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAll(flusher, emitter).WaitAsync(TimeSpan.FromSeconds(30));
            bridge.Detach(sessionId);

            var texts = published.Snapshot().Where(e => ((AgentAssistTranscriptEvent)e).SessionId == sessionId)
                .Select(TextOf).ToList();
            var expected = Enumerable.Range(0, 96).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (!texts.SequenceEqual(expected))
            {
                var at = Enumerable.Range(0, Math.Min(texts.Count, expected.Count)).FirstOrDefault(i => texts[i] != expected[i]);
                throw new Xunit.Sdk.XunitException(
                    $"held events out of emission order in round {round}: at {at} got {(at < texts.Count ? texts[at] : "<none>")}, expected {expected[at]} ({texts.Count} published)");
            }
        }
    }

    [Fact]
    public async Task Emit_ShouldCarryTheStampedTenant_WhenTheStampLandsDuringAnEmission()
    {
        var (bus, published) = LocalBus();
        var bridge = Bridge(bus);

        for (var round = 0; round < 50; round++)
        {
            var sessionId = "as-stamp-" + round.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var session = Start(bridge, startTenant: "", callTenant: null, sessionId);

            using var barrier = new Barrier(2);
            var emitter = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                for (var i = 0; i < 40; i++)
                    session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var stamper = Task.Factory.StartNew(() =>
            {
                barrier.SignalAndWait();
                session.Call.TenantId = Demo;
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await Task.WhenAll(emitter, stamper).WaitAsync(TimeSpan.FromSeconds(30));
            Advance(BindCheck);
            bridge.Detach(sessionId);

            var mine = published.Snapshot().Where(e => ((AgentAssistTranscriptEvent)e).SessionId == sessionId).ToList();
            foreach (var e in mine.Where(e => e.TenantId != Demo))
                throw new Xunit.Sdk.XunitException($"expected tenantId {Demo} on every event of round {round}, got '{e.TenantId}' on '{TextOf(e)}'");
            mine.Select(TextOf).Should().Equal(
                Enumerable.Range(0, 40).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "none is lost");
        }
    }

    [Fact]
    public async Task Emit_ShouldReachTheLocalSubscribersAndThePushBusInOneOrder_WhenABoundSessionPublishesFromTwoThreads()
    {
        var push = new RecordingPushBus();
        var bus = new PlatformEventBus(push);
        _disposables.Add(bus);
        var local = new Recorder();
        _disposables.Add(bus.Events.Subscribe(e => local.Add(e)));
        var session = Start(Bridge(bus), startTenant: Demo, callTenant: Demo);

        using var barrier = new Barrier(2);
        var transcripts = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                barrier.SignalAndWait();
                session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var sentiments = Task.Factory.StartNew(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                barrier.SignalAndWait();
                session.Feel(SentimentLabel.Neutral);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await Task.WhenAll(transcripts, sentiments).WaitAsync(TimeSpan.FromSeconds(60));

        var l = local.Snapshot();
        var p = push.Snapshot();
        l.Should().HaveCount(2000);
        p.Should().HaveCount(2000);
        for (var i = 0; i < l.Count; i++)
        {
            if (!ReferenceEquals(l[i], p[i]))
                throw new Xunit.Sdk.XunitException(
                    $"local and push-bus order differ at {i}: local {l[i].Type} '{TextOf(l[i])}', push bus {((PlatformEvent)p[i]).Type} '{TextOf((PlatformEvent)p[i])}'");
        }
    }

    [Fact]
    public void Emit_ShouldPublishEachEventOnceAndGoOn_WhenALocalSubscriberThrowsOnOneTranscript()
    {
        var push = new RecordingPushBus();
        var bus = new PlatformEventBus(push);
        _disposables.Add(bus);
        // The defective subscriber comes first, so its throw stops the publish of that one event; Rx drops it after it.
        _disposables.Add(bus.Events.Subscribe(e =>
        {
            if (e is AgentAssistTranscriptEvent { Text: "boom" })
                throw new InvalidOperationException("subscriber defect");
        }));
        var local = new Recorder();
        _disposables.Add(bus.Events.Subscribe(e => local.Add(e)));
        var session = Start(Bridge(bus), startTenant: Demo, callTenant: Demo);

        session.Say("before");
        session.Say("boom");
        session.Say("after");
        session.Feel(SentimentLabel.Neutral);
        Advance(TimeSpan.FromSeconds(2));

        push.Snapshot().Select(e => TextOf((PlatformEvent)e)).Should().Equal(["before", "after", "Neutral"],
            "the failed transcript reaches the push bus at most once and the later events go on in order");
        local.Snapshot().Select(TextOf).Should().Equal(["before", "after", "Neutral"]);
        _logger.Count("PublishTranscriptFailed").Should().Be(1, "one attempt, logged once");
    }

    [Fact]
    public async Task Emit_ShouldDeliverAllHeldEventsToThePushBusAndAStream_WhenAFullHoldIsFlushed()
    {
        using var pushMetrics = new PushMetrics();
        using var rx = new RxPushEventBus(
            Options.Create(new PushEventBusOptions()), NullLogger<RxPushEventBus>.Instance, pushMetrics);
        long bufferFull = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, pushMetrics.EventsDropped))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag is { Key: "reason", Value: "buffer_full" })
                    Interlocked.Add(ref bufferFull, value);
        });
        listener.Start();

        var received = new Recorder();
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rxSub = rx.AsObservable().Subscribe(e =>
        {
            if (received.Add((PlatformEvent)e) == 128)
                all.TrySetResult();
        });

        var bus = new PlatformEventBus(rx);
        _disposables.Add(bus);

        // A live stream's buffer: 256, drop-oldest, filled from the bus as the SSE endpoint fills it.
        var stream = Channel.CreateBounded<PlatformEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        using var streamSub = bus.Events.Subscribe(e => stream.Writer.TryWrite(e));

        var session = Start(Bridge(bus), startTenant: "", callTenant: null);
        for (var i = 0; i < 128; i++)
            session.Say(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        session.Call.TenantId = Demo;
        Advance(BindCheck);

        await all.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var expected = Enumerable.Range(0, 128).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
        received.Snapshot().Select(TextOf).Should().Equal(expected, "the push bus receives all 128 in emission order");
        var streamed = new List<string>();
        while (stream.Reader.TryRead(out var e))
            streamed.Add(TextOf(e));
        streamed.Should().Equal(expected, "the stream receives all 128 in emission order");
        listener.RecordObservableInstruments();
        Interlocked.Read(ref bufferFull).Should().Be(0);
    }

    // ─── Test doubles ───────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Recorder
    {
        private readonly Lock _gate = new();
        private readonly List<PlatformEvent> _events = [];

        public int Add(PlatformEvent e)
        {
            lock (_gate)
            {
                _events.Add(e);
                return _events.Count;
            }
        }

        public IReadOnlyList<PlatformEvent> Snapshot()
        {
            lock (_gate)
                return [.. _events];
        }
    }

    private sealed class RecordingPushBus : IPushEventBus
    {
        private readonly Lock _gate = new();
        private readonly List<PushEvent> _events = [];

        public ValueTask PublishAsync<TEvent>(TEvent pushEvent, CancellationToken ct = default) where TEvent : PushEvent
        {
            lock (_gate)
                _events.Add(pushEvent);
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<PushEvent> Snapshot()
        {
            lock (_gate)
                return [.. _events];
        }

        public IObservable<PushEvent> AsObservable() => throw new NotSupportedException();

        public IObservable<TEvent> OfType<TEvent>() where TEvent : PushEvent => throw new NotSupportedException();
    }

    private sealed class HeldDrops : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _gate = new();
        private readonly Dictionary<string, long> _byReason = new(StringComparer.Ordinal);

        public HeldDrops(AgentAssistHeldEventMetrics metrics)
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter)
                    && instrument.Name == AgentAssistHeldEventMetrics.HeldDroppedInstrument)
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "reason" && tag.Value is string reason)
                    {
                        lock (_gate)
                            _byReason[reason] = _byReason.GetValueOrDefault(reason) + value;
                    }
                }
            });
            _listener.Start();
        }

        public long Of(string reason)
        {
            lock (_gate)
                return _byReason.GetValueOrDefault(reason);
        }

        public long Total
        {
            get
            {
                lock (_gate)
                    return _byReason.Values.Sum();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RecordingLogger : ILogger<AgentAssistBridge>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _names = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _names.Add(eventId.Name ?? string.Empty);
        }

        public int Count(string name)
        {
            lock (_gate)
                return _names.Count(n => n == name);
        }
    }

    /// <summary>A <see cref="FakeTimeProvider"/> that counts the timers created on it and not yet disposed.</summary>
    private sealed class CountingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private int _live;

        public int LiveTimers => Volatile.Read(ref _live);

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _live);
            return new CountedTimer(inner.CreateTimer(callback, state, dueTime, period), this);
        }

        private sealed class CountedTimer(ITimer timer, CountingTimeProvider owner) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref owner._live);
                timer.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
