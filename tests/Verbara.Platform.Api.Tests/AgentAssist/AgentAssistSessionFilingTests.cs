using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Platform.Queues;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D12, Monitor list): the record the engine's replica keeps for each live session,
/// filed under the owner tenant re-evaluated at each bind check and heartbeat, on a shared store whose clock is a
/// <see cref="FakeTimeProvider"/>. Every replica reads the same store, so a read here is the read of any replica.
/// </summary>
public sealed class AgentAssistSessionFilingTests : IDisposable
{
    private const string Demo = "demo";
    private const string Acme = "acme";
    private const string SessionId = "as-filing-1";
    private const string LinkedId = "1712345678.50";
    private const string DemoConversation = "c0000000000000000000000000000dem";
    private const string AcmeConversation = "c0000000000000000000000000000acm";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
    private readonly MemoryLiveSessionStore _store;
    private readonly IConversationStore _conversations = Substitute.For<IConversationStore>();
    private readonly IAgentStore _agents = NoAgents();

    private static IAgentStore NoAgents()
    {
        var agents = Substitute.For<IAgentStore>();
        agents.GetByIdsAsync(Arg.Any<TenantId>(), Arg.Any<IReadOnlyCollection<EntityId>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Agent>());
        return agents;
    }
    private readonly List<AgentAssistSessionFiling> _filings = [];

    public AgentAssistSessionFilingTests()
    {
        _store = new MemoryLiveSessionStore(_time);
        _conversations.FindByVoiceLinkedIdAsync(new TenantId(Demo), LinkedId, Arg.Any<CancellationToken>())
            .Returns(Tracked(Demo, DemoConversation));
        _conversations.FindByVoiceLinkedIdAsync(new TenantId(Acme), LinkedId, Arg.Any<CancellationToken>())
            .Returns(Tracked(Acme, AcmeConversation));
    }

    public void Dispose()
    {
        foreach (var filing in _filings)
            filing.Dispose();
    }

    private static Conversation Tracked(string tenant, string id) => new()
    {
        ConversationId = EntityId.From(id), TenantId = new TenantId(tenant), ContactId = EntityId.New(),
        Channel = ChannelType.Voice, State = ConversationState.Active, CreatedAt = DateTimeOffset.UnixEpoch, VoiceLinkedId = LinkedId,
    };

    private static CallSession Call(string? tenant) =>
        new("call-filing-1", LinkedId, "server-1", CallDirection.Inbound) { TenantId = tenant, AgentId = "agent-7", QueueName = "support" };

    private async Task<AgentAssistSessionFiling> StartAsync(string startTimeTenant, CallSession call)
    {
        var filing = AgentAssistSessionFiling.Start(SessionId, startTimeTenant, call, _store, _time, NullLogger.Instance);
        _filings.Add(filing);
        await filing.WhenIdleAsync();
        return filing;
    }

    private static async Task AdvanceAsync(FakeTimeProvider time, AgentAssistSessionFiling filing, TimeSpan by)
    {
        time.Advance(by);
        await filing.WhenIdleAsync();
    }

    /// <summary><c>GET /supervisor/sessions/active</c> as a supervisor of <paramref name="tenant"/> reads it, on any replica.</summary>
    private async Task<ActiveSessionDto[]> ListAsync(string tenant) =>
        await SupervisorEndpoints.ToActiveSessionsAsync(
            await _store.ListAsync(tenant, CancellationToken.None), new TenantId(tenant), _conversations, _agents, CancellationToken.None);

    [Fact]
    public async Task Filing_ShouldListTheSessionToItsTenant_WhenItsTenantIsKnownAtStart()
    {
        await StartAsync(Demo, Call(Demo));

        var row = (await ListAsync(Demo)).Should().ContainSingle().Subject;
        row.Should().Be(new ActiveSessionDto(SessionId, "agent-7", null, "support", null, null, DemoConversation, null));
        (await ListAsync(Acme)).Should().BeEmpty("another tenant never sees it");
    }

    [Fact]
    public async Task Filing_ShouldDropTheRow_WhenItsHeartbeatsStopForLongerThanTheRecordLives()
    {
        var filing = await StartAsync(Demo, Call(Demo));

        await AdvanceAsync(_time, filing, TimeSpan.FromSeconds(20));
        (await ListAsync(Demo)).Should().ContainSingle("each 5 s heartbeat keeps a live session's row");

        filing.Dispose();   // the replica that ran it is gone: no more heartbeats
        _time.Advance(AgentAssistSessionFiling.RecordTtl);

        (await ListAsync(Demo)).Should().BeEmpty("a record lives 15 s past its last heartbeat");
        _store.IndexMembers(Demo).Should().BeEmpty("the reader drops the expired member");
    }

    [Fact]
    public async Task Filing_ShouldFileTheRowUnderTheStampedTenant_AtTheNextBindCheckAfterALateStamp()
    {
        var call = Call(tenant: null);
        var filing = await StartAsync(string.Empty, call);
        (await ListAsync(Demo)).Should().BeEmpty("precondition: no owner, so no row");

        call.TenantId = Demo;
        await AdvanceAsync(_time, filing, AgentAssistSessionFiling.BindCheckInterval);

        (await ListAsync(Demo)).Should().ContainSingle("a late stamp files the row within one 250 ms bind check");
    }

    [Fact]
    public async Task Filing_ShouldRemoveTheRow_WhenTheCallIsRestampedToAConflictingTenantAndTheSessionEmits()
    {
        var call = Call(Demo);
        var filing = await StartAsync(Demo, call);

        call.TenantId = Acme;
        filing.OnEmission();
        await filing.WhenIdleAsync();

        (await ListAsync(Demo)).Should().BeEmpty("a conflict lists the session to nobody");
        (await ListAsync(Acme)).Should().BeEmpty("and never to the other tenant");
        _store.IndexMembers(Demo).Should().BeEmpty();
    }

    /// <summary>
    /// The cross-replica read before the heartbeat, for both start-time tenants. Host A files a
    /// bound session under <c>demo</c>, its call is re-stamped <c>acme</c>, the session emits nothing, and the list is
    /// read at once, then after one heartbeat (5 s).
    /// <list type="bullet">
    /// <item>start-time tenant <c>demo</c>: a conflict. Read at once, <c>demo</c> still sees the row with its pre-stamp
    /// members and <c>acme</c> none; after the heartbeat neither sees it and the index member is gone.</item>
    /// <item>start-time tenant empty: <c>acme</c> is the new owner. After the heartbeat the row is <c>acme</c>'s, and it
    /// carries <c>acme</c>'s conversation, never the one <c>demo</c>'s list showed: the conversation is resolved in the
    /// reader's tenant when the list is read, never stored with the record.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData(Demo)]
    [InlineData("")]
    public async Task Filing_ShouldReachTheRestampsOwnerWithinOneHeartbeat_WhenTheStartTimeTenantIs(string startTimeTenant)
    {
        var call = Call(Demo);
        var filing = await StartAsync(startTimeTenant, call);
        (await ListAsync(Demo)).Should().ContainSingle().Which.ConversationId.Should().Be(DemoConversation, "precondition");

        call.TenantId = Acme;

        var demoAtOnce = (await ListAsync(Demo)).Should().ContainSingle(
            "until the next heartbeat (at most 5 s) the row stays filed under the tenant that owned it").Subject;
        demoAtOnce.Should().Be(new ActiveSessionDto(SessionId, "agent-7", null, "support", null, null, DemoConversation, null),
            "with its members from before the re-stamp");
        (await ListAsync(Acme)).Should().BeEmpty("it is never filed under the other tenant before the leader re-evaluates it");

        await AdvanceAsync(_time, filing, AgentAssistSessionFiling.HeartbeatInterval);

        (await ListAsync(Demo)).Should().BeEmpty($"one 5 s heartbeat re-evaluates the owner (start-time tenant '{startTimeTenant}')");
        _store.IndexMembers(Demo).Should().BeEmpty("the former owner's index member is gone after the 5 s heartbeat");
        if (startTimeTenant == Demo)
        {
            (await ListAsync(Acme)).Should().BeEmpty("start-time tenant demo and call tenant acme conflict: listed to nobody");
        }
        else
        {
            var moved = (await ListAsync(Acme)).Should().ContainSingle(
                "with no start-time tenant the call's tenant owns the session, so the row moves to acme").Subject;
            moved.ConversationId.Should().Be(AcmeConversation,
                "the new owner's row resolves the conversation in its own tenant and never shows demo's");
        }
    }

    [Fact]
    public async Task Filing_ShouldListTheSessionToNobody_WhenItNeverHasAnOwner()
    {
        var filing = await StartAsync(string.Empty, Call(tenant: null));

        await AdvanceAsync(_time, filing, TimeSpan.FromSeconds(6));

        (await ListAsync(Demo)).Should().BeEmpty();
        (await ListAsync(Acme)).Should().BeEmpty();
        _store.Writes.Should().Be(0, "an ownerless session is never filed");
    }

    /// <summary>Pro's setup fence: SessionEnded right after SessionStarted, nothing between.</summary>
    [Fact]
    public async Task Filing_ShouldLeaveNoRowAndRewriteNothing_WhenTheSessionEndsRightAfterItStarted()
    {
        var filing = await StartAsync(Demo, Call(Demo));

        await filing.EndAsync();
        var writes = _store.Writes;
        await AdvanceAsync(_time, filing, AgentAssistSessionFiling.HeartbeatInterval);

        (await ListAsync(Demo)).Should().BeEmpty();
        _store.IndexMembers(Demo).Should().BeEmpty("no index member is left");
        _store.Writes.Should().Be(writes, "a heartbeat tick released after the delete re-writes nothing");
        _time.Advance(TimeSpan.FromMinutes(1));
        _store.Writes.Should().Be(writes, "no timer is left");
    }
}
