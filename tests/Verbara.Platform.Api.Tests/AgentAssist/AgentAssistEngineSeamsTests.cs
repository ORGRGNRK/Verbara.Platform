using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end, task B.4 (design D4): the activation gate and the snoop app the Api gives the real Pro
/// engine, through the Api's own composition (<see cref="ProEngineReplica"/>). A replica that is not the voice AMI
/// leader keeps a connected call from its engine before any ARI request; the leader runs it, and the engine asks for
/// both snoops in the replica's own node app (<c>StasisAppNames.NodeInstance</c>), never in another.
/// </summary>
public sealed class AgentAssistEngineGateAndSnoopTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Engine_ShouldGateAConnectedCallBeforeAnySnoop_WhenTheReplicaIsNotTheVoiceLeader()
    {
        using var guard = new CancellationTokenSource(Bound);
        var call = new CallSession("aa-seam-follower", "1712345678.50", ProEngineReplica.ServerId, CallDirection.Inbound);
        await using var follower = await ProEngineReplica.StartAsync("replica-f", isVoiceLeader: false, call, core: null, guard.Token);

        // Whichever comes first: the gate's count, or a session the gate should have prevented.
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (follower.Supervisor.SessionStarted.Subscribe(_ => started.TrySetResult()))
        {
            follower.Connect(call);
            await Task.WhenAny(
                    follower.Measurements.SeenAsync(m => m.Instrument == "agentassist.sessions.gated"), started.Task)
                .WaitAsync(guard.Token);
        }

        started.Task.IsCompleted.Should().BeFalse("a follower's engine must not start a session for the call");
        follower.Measurements.Sum("agentassist.sessions.gated").Should().Be(1,
            "the voice-leader gate keeps the call from a follower's engine");
        follower.SnoopApps.Should().BeEmpty("a gated call reaches no ARI request");
        follower.Supervisor.ActiveSessions.Should().BeEmpty();
        follower.Measurements.Sum("agentassist.sessions.started").Should().Be(0);
    }

    [Fact]
    public async Task Engine_ShouldSnoopBothSidesInTheReplicasOwnNodeApp_WhenTheReplicaIsTheVoiceLeader()
    {
        using var guard = new CancellationTokenSource(Bound);
        var call = new CallSession("aa-seam-leader", "1712345678.51", ProEngineReplica.ServerId, CallDirection.Inbound);
        await using var leader = await ProEngineReplica.StartAsync("replica-l", isVoiceLeader: true, call, core: null, guard.Token);

        await leader.ConnectAndAwaitSessionAsync(call, guard.Token);

        leader.Measurements.Sum("agentassist.sessions.gated").Should().Be(0, "the voice leader's gate is open");
        leader.SnoopApplication.Should().Be("verbara-node-replica-l",
            "Program.cs passes StasisAppNames.NodeInstance: the node app prefix, '-', the replica's instance id");
        leader.SnoopApps.Should().Equal(["verbara-node-replica-l", "verbara-node-replica-l"],
            "both snoops (caller and agent side) are created in the app this replica's node client holds, so Asterisk " +
            "hands their Stasis events to this replica and to no other");

        await leader.EndAndAwaitSessionEndAsync(call, guard.Token);
    }
}

/// <summary>
/// agent-assist-works-end-to-end, task B.4 (design D4): two replicas' real Pro engines, each composed by the Api with the
/// Postgres per-call claim over one core database (a Testcontainers <c>postgres:18-alpine</c> with the embedded
/// <c>Storage.Postgres</c> migrations), see the same physical call connect. Both are voice leaders, the window the
/// gate cannot close (an old holder whose renewal is pending): the first takes the claim and runs the session, the
/// second is refused (<c>agentassist.sessions.skipped</c>, reason <c>claim_held</c>) before any ARI request, and the
/// claim's row is deleted when the first replica's session ends.
/// </summary>
public sealed class AgentAssistEngineCallClaimLiveDbTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private IContainer? _postgres;
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        _postgres = new ContainerBuilder("postgres:18-alpine")
            .WithEnvironment("POSTGRES_PASSWORD", "postgres")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            // `-h 127.0.0.1` probes over TCP: the entrypoint first runs a socket-only temporary server.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilCommandIsCompleted("pg_isready", "-U", "postgres", "-h", "127.0.0.1"))
            .Build();
        await _postgres.StartAsync();
        _db = NpgsqlDataSource.Create($"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres");
        await PostgresAgentAssistCallClaimLiveDbTests.ApplyMigrationsAsync(_db);
    }

    public async Task DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Engines_ShouldRunOneSessionPerCallAndReleaseTheClaim_WhenTwoReplicasSeeTheSameCall()
    {
        using var guard = new CancellationTokenSource(Bound);
        var linkedId = "1712345678." + Guid.NewGuid().ToString("N")[..8];
        var call = new CallSession("aa-seam-claim", linkedId, ProEngineReplica.ServerId, CallDirection.Inbound);
        await using var a = await ProEngineReplica.StartAsync("replica-a", isVoiceLeader: true, call, _db, guard.Token);
        await using var b = await ProEngineReplica.StartAsync("replica-b", isVoiceLeader: true, call, _db, guard.Token);

        await a.ConnectAndAwaitSessionAsync(call, guard.Token);

        var row = await ClaimOwnerAsync(call);
        row.Should().Be("replica-a", "the engine that runs the session holds the call's claim in the core database");
        a.SnoopApps.Should().Equal(["verbara-node-replica-a", "verbara-node-replica-a"]);

        b.Connect(call);
        await b.Measurements.SeenAsync(m => m.Is("agentassist.sessions.skipped", "reason", "claim_held"))
            .WaitAsync(guard.Token);

        b.Measurements.Sum("agentassist.sessions.skipped", "reason", "claim_held").Should().Be(1,
            "the second replica's engine is refused the call another replica holds");
        b.Measurements.Sum(AgentAssistCallClaimMetrics.CallClaimInstrument, "outcome", AgentAssistCallClaimMetrics.Held)
            .Should().Be(1, "the refusal came from the Postgres claim");
        b.SnoopApps.Should().BeEmpty("a refused claim stops the setup before any ARI request");
        b.Supervisor.ActiveSessions.Should().BeEmpty();
        a.Supervisor.ActiveSessions.Keys.Should().Equal([call.SessionId]);
        (await ClaimOwnerAsync(call)).Should().Be("replica-a", "the refused replica leaves the holder's claim alone");

        var released = a.Measurements.SeenAsync(m =>
            m.Is(AgentAssistCallClaimMetrics.CallClaimInstrument, "outcome", AgentAssistCallClaimMetrics.Released));
        await a.EndAndAwaitSessionEndAsync(call, guard.Token);
        await released.WaitAsync(guard.Token);

        (await ClaimOwnerAsync(call)).Should().BeNull("the session's end releases the call's claim");
    }

    private async Task<string?> ClaimOwnerAsync(CallSession call)
    {
        await using var cmd = _db.CreateCommand("SELECT owner FROM agent_assist_call_claims WHERE call_key = @k");
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = call.ServerId + "/" + call.LinkedId });
        return (string?)await cmd.ExecuteScalarAsync();
    }
}
