using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Serialization;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D13, fixture <c>active-session.v2.json</c>): each row of
/// <c>GET /supervisor/sessions/active</c> names its agent the way the session's live events do
/// (<see cref="AgentAssistBridge.DeriveAgentId"/>), with the agent's display name in the caller's tenant, and carries the
/// label of the session's latest caller sentiment reading. <c>agentName</c> and <c>sentiment</c> are always written,
/// null when unknown.
/// </summary>
public sealed class ActiveSessionAgentAndSentimentTests : IDisposable
{
    private const string Acme = "acme";
    private const string AgentGuid = "0651f84b6f284a1c9b8d658330fa8310";
    private const string LinkedId = "1712345678.40";

    private static readonly TenantId Caller = new(Acme);

    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IAgentStore _agents = Substitute.For<IAgentStore>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
    private readonly List<AgentAssistSessionFiling> _filings = [];

    public ActiveSessionAgentAndSentimentTests()
    {
        _agents.GetByIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Agent>());
        _agents.GetByIdsAsync(
                Caller,
                Arg.Is<IReadOnlyCollection<EntityId>>(ids => ids.Contains(EntityId.From(AgentGuid))),
                Arg.Any<CancellationToken>())
            .Returns([AgentRow(Caller, AgentGuid, "Ana Gómez")]);
    }

    public void Dispose()
    {
        foreach (var filing in _filings)
            filing.Dispose();
    }

    private static Agent AgentRow(TenantId tenant, string agentId, string displayName) => new()
    {
        AgentId = EntityId.From(agentId), TenantId = tenant, UserId = EntityId.New(), DisplayName = displayName,
        State = AgentState.Available, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static CallSession Call(string? agentInterface, string? rawAgentId = null) =>
        new("call-d13", LinkedId, "server-1", CallDirection.Inbound)
        {
            TenantId = Acme,
            AgentInterface = agentInterface,
            AgentId = rawAgentId,
            QueueName = "acme-support",
        };

    private Task<ActiveSessionDto[]> ListAsync(params LiveSessionRecord[] records) =>
        SupervisorEndpoints.ToActiveSessionsAsync(records, Caller, _conversations, _agents, CancellationToken.None);

    [Fact]
    public async Task GetActiveSessions_ShouldCarryTheAgentsIdAndName_WhenTheMemberInterfaceParses()
    {
        var call = Call($"PJSIP/{Acme}-agent-{AgentGuid}");

        var row = (await ListAsync(LiveSessionRecord.Of("as-d13-parsed", Acme, call))).Should().ContainSingle().Subject;

        row.AgentId.Should().Be(AgentGuid, "the id parsed from the queue-member interface");
        row.AgentId.Should().Be(AgentAssistBridge.DeriveAgentId(Acme, call.AgentInterface, call.AgentId),
            "the list and the live events name the same agent through one helper");
        row.AgentName.Should().Be("Ana Gómez", "the agent's display name in the caller's tenant");
    }

    [Theory]
    [InlineData("PJSIP/globex-agent-0651f84b6f284a1c9b8d658330fa8310")]   // another tenant's endpoint
    [InlineData("PJSIP/acme-agent-sup-u1")]                                // a supervisor listen endpoint
    [InlineData("SIP/1001")]                                               // not an agent endpoint
    [InlineData(null)]
    public async Task GetActiveSessions_ShouldWriteNullAgentIdAndName_WhenTheInterfaceDoesNotParse(string? agentInterface)
    {
        var items = await ListAsync(LiveSessionRecord.Of("as-d13-unparsed", Acme, Call(agentInterface)));

        var row = items.Should().ContainSingle().Subject;
        row.AgentId.Should().BeNull();
        row.AgentName.Should().BeNull();
        var json = JsonNode.Parse(JsonSerializer.Serialize(items, ApiJsonContext.Default.ActiveSessionDtoArray))![0]!.AsObject();
        json.ContainsKey("agentId").Should().BeTrue("null is written, never omitted");
        json.ContainsKey("agentName").Should().BeTrue("null is written, never omitted");
        json["agentId"].Should().BeNull();
        json["agentName"].Should().BeNull();
    }

    [Fact]
    public async Task GetActiveSessions_ShouldWriteNullAgentName_WhenNoAgentRowResolvesInTheCallersTenant()
    {
        var call = Call(agentInterface: null, rawAgentId: "7d1e0f5a2b3c4d5e6f708192a3b4c5d6");

        var row = (await ListAsync(LiveSessionRecord.Of("as-d13-norow", Acme, call))).Should().ContainSingle().Subject;

        row.AgentId.Should().Be("7d1e0f5a2b3c4d5e6f708192a3b4c5d6", "the call's own agent id is the helper's fallback");
        row.AgentName.Should().BeNull("no agent row resolves in the caller's tenant");
    }

    [Fact]
    public async Task GetActiveSessions_ShouldWriteNullSentiment_WhenNoReadingYet()
    {
        var items = await ListAsync(LiveSessionRecord.Of("as-d13-nosentiment", Acme, Call(null)));

        items.Should().ContainSingle().Which.Sentiment.Should().BeNull();
        var json = JsonNode.Parse(JsonSerializer.Serialize(items, ApiJsonContext.Default.ActiveSessionDtoArray))![0]!.AsObject();
        json.ContainsKey("sentiment").Should().BeTrue("null is written, never omitted");
        json["sentiment"].Should().BeNull();
    }

    [Fact]
    public async Task InProcessList_ShouldCarryTheSessionsSentimentAndDerivedAgent_WhenTheEngineRunsHere()
    {
        var live = new FakeLiveSessions();
        var call = Call($"PJSIP/{Acme}-agent-{AgentGuid}");
        live.Add(AgentAssistLiveSessions.ToLive("as-d13-inproc", call, Acme, "Frustrated"));
        var store = new InProcessAgentAssistLiveSessionStore(live);

        var row = (await ListAsync([.. await store.ListAsync(Acme, CancellationToken.None)])).Should().ContainSingle().Subject;

        row.Sentiment.Should().Be("Frustrated");
        row.AgentId.Should().Be(AgentGuid);
        row.AgentName.Should().Be("Ana Gómez");
    }

    [Fact]
    public async Task Filing_ShouldRefileTheLatestSentimentAtOnce_WhenAReadingArrivesBetweenHeartbeats()
    {
        var store = new MemoryLiveSessionStore(_time);
        string? latest = null;
        var filing = AgentAssistSessionFiling.Start(
            "as-d13-filing", Acme, Call($"PJSIP/{Acme}-agent-{AgentGuid}"), store, _time, NullLogger.Instance, () => latest);
        _filings.Add(filing);
        await filing.WhenIdleAsync();

        async Task<ActiveSessionDto> RowAsync() =>
            (await ListAsync([.. await store.ListAsync(Acme, CancellationToken.None)])).Should().ContainSingle().Subject;

        var before = await RowAsync();
        before.Sentiment.Should().BeNull("no reading yet");
        before.AgentId.Should().Be(AgentGuid);
        before.AgentName.Should().Be("Ana Gómez");

        latest = "Frustrated";
        filing.OnEmission();
        await filing.WhenIdleAsync();
        (await RowAsync()).Sentiment.Should().Be("Frustrated", "an event after a new reading re-files the record, no heartbeat needed");

        var writes = store.Writes;
        filing.OnEmission();
        await filing.WhenIdleAsync();
        store.Writes.Should().Be(writes, "an event that changes neither owner nor sentiment writes nothing");
    }
}
