using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Pro.AgentAssist.Domain;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Sessions;
using Xunit.Abstractions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D15, the <c>BridgeEmission</c> leg): a bound session's emission path allocates
/// what the event itself needs and almost nothing more. The floor is the same four records built and published straight
/// on the bus; the bound path goes through the bridge and <c>AgentAssistSessionIdentity</c> (bind check, lock, record
/// built with the bound identity and its emission time). D15 predicts at most 64 B per event over the floor; the test holds
/// the measured figure instead (see <see cref="MaxBytesPerEventOverFloor"/>).
/// </summary>
public sealed class AgentAssistBoundEmissionAllocationTests(ITestOutputHelper output) : IDisposable
{
    private const string Tenant = "acme";
    private const string LinkedId = "1712345678.77";
    private const string ConversationId = "c0000000000000000000000000000acm";
    private const string AgentGuid = "11111111111111111111111111111111";
    private const string SessionId = "as-alloc-1";
    private const int Rounds = 2_000;

    /// <summary>
    /// The measured 36 B per event over the floor (the same in every run: the counter is exact per thread) plus a 4 B
    /// margin. The figure is an average over the four event kinds, so one extra heap object (24 B at least) on one
    /// factory adds 6 B per event and goes over, and a per-emission capturing closure on one factory adds 24 B (60 B per
    /// event, which D15's 64 B line let through). The margin absorbs up to 16 B per round of four, an object-alignment
    /// change in a runtime update, not a new allocation.
    /// </summary>
    private const int MaxBytesPerEventOverFloor = 40;

    private static readonly string Text120 = new('x', 120);
    private static readonly string[] TriggerWords = ["again", "third time"];
    private static readonly TranscriptSegment Segment = new(Speaker.Caller, Text120, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static readonly AgentSuggestion Suggestion = new("sg-01", Text120, SuggestionPriority.Important, SuggestionSource.Keyword);
    private static readonly SentimentReading Reading = new(Speaker.Caller, -0.62f, SentimentLabel.Frustrated, TriggerWords, DateTimeOffset.UnixEpoch);
    private static readonly ComplianceAlert Alert = new("cr-identity-check", "full card number", ComplianceSeverity.Warning, DateTimeOffset.UnixEpoch);

    private readonly PlatformEventBus _bus = new();
    private readonly AgentAssistSupervisor _supervisor = new();
    private readonly Subject<TranscriptSegment> _transcripts = new();
    private readonly Subject<AgentSuggestion> _suggestions = new();
    private readonly Subject<SentimentReading> _sentiment = new();
    private readonly Subject<ComplianceAlert> _compliance = new();
    private readonly List<IDisposable> _disposables = [];
    private long _published;
    private PlatformEvent? _last;

    public void Dispose()
    {
        for (var i = _disposables.Count - 1; i >= 0; i--)
            _disposables[i].Dispose();
        _transcripts.Dispose();
        _suggestions.Dispose();
        _sentiment.Dispose();
        _compliance.Dispose();
        _supervisor.Dispose();
        _bus.Dispose();
    }

    [Fact]
    public void Emit_ShouldAllocateAtMost40BytesPerEventOverTheFloor_WhenTheSessionIsBound()
    {
        BindSession();

        // Warm both paths so the measured rounds run the same code (no first-call allocations in either).
        Floor(Rounds);
        Bound(Rounds);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Floor(Rounds);
        var floorBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var published = _published;
        before = GC.GetAllocatedBytesForCurrentThread();
        Bound(Rounds);
        var boundBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        (_published - published).Should().Be(4L * Rounds, because: "every bound emission is published once, none held");
        _last.Should().BeOfType<AgentAssistTranscriptEvent>()
            .Which.Should().Match<AgentAssistTranscriptEvent>(t =>
                t.TenantId == Tenant && t.AgentId == AgentGuid && t.ConversationId == ConversationId);

        var floorPerEvent = floorBytes / (4.0 * Rounds);
        var boundPerEvent = boundBytes / (4.0 * Rounds);
        output.WriteLine($"floor {floorPerEvent:F1} B/event, bound {boundPerEvent:F1} B/event, over floor {boundPerEvent - floorPerEvent:F1} B/event");
        (boundPerEvent - floorPerEvent).Should().BeLessThanOrEqualTo(MaxBytesPerEventOverFloor,
            because: "the bound path builds each record once, in place, with no per-emission closure (measured 36 B per event over the floor; floor {0:F1} B, bound {1:F1} B per event)",
            floorPerEvent, boundPerEvent);
    }

    private void BindSession()
    {
        _disposables.Add(_bus.Events.Subscribe(e =>
        {
            _published++;
            _last = e;
        }));

        var conversations = Substitute.For<IConversationStore>();
        conversations.FindByVoiceLinkedIdAsync(new TenantId(Tenant), LinkedId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Conversation?>(new Conversation
            {
                ConversationId = EntityId.From(ConversationId), TenantId = new TenantId(Tenant), ContactId = EntityId.New(),
                Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch,
                VoiceLinkedId = LinkedId,
            }));

        var bridge = new AgentAssistBridge(_supervisor, _bus, conversations, NullLogger<AgentAssistBridge>.Instance);
        _disposables.Add(bridge);
        var call = new CallSession("call-alloc-1", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = Tenant, AgentInterface = $"PJSIP/{Tenant}-agent-{AgentGuid}", QueueName = "support",
        };
        bridge.Attach(new AgentAssistBridge.AssistSessionFeed(
            SessionId, StartTenant: null, call, _transcripts, _suggestions, _sentiment, _compliance));

        // The lookup answers synchronously, so the first emission is already bound and published, not held.
        var published = _published;
        _transcripts.OnNext(Segment);
        (_published - published).Should().Be(1, because: "a session whose tenant and conversation are known is bound at once");
    }

    private void Floor(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            _bus.Publish(new AgentAssistSuggestionEvent(Tenant, SessionId, AgentGuid, ConversationId, "sg-01", Text120, "Important", "Keyword", null));
            _bus.Publish(new AgentAssistSentimentEvent(Tenant, SessionId, AgentGuid, ConversationId, "Caller", -0.62f, "Frustrated", TriggerWords));
            _bus.Publish(new AgentAssistComplianceAlertEvent(Tenant, SessionId, AgentGuid, ConversationId, "cr-identity-check", "full card number", "Warning"));
            _bus.Publish(new AgentAssistTranscriptEvent(Tenant, SessionId, AgentGuid, ConversationId, "Caller", Text120, IsFinal: true));
        }
    }

    private void Bound(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            _suggestions.OnNext(Suggestion);
            _sentiment.OnNext(Reading);
            _compliance.OnNext(Alert);
            _transcripts.OnNext(Segment);
        }
    }
}
