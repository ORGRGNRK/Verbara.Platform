using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.1a (design D4): the per-call claim over a Testcontainers Postgres with the real
/// embedded <c>Storage.Postgres</c> migrations. One claimant wins a race, the same owner's later setup keeps the claim
/// in either commit order, a release removes only its own claim, an ambiguous claim is undone by its release, another
/// owner waits for the 24 h abandon age, the hourly sweep removes only abandoned rows, and an unreachable database
/// fails the claim and never fails the release.
/// </summary>
/// <remarks>
/// The tests of this class run one after another (one xunit collection), so the sweep of one never removes a row
/// another test aged. Every test uses call keys of its own.
/// </remarks>
public sealed class PostgresAgentAssistCallClaimLiveDbTests : IAsyncLifetime
{
    private const string OwnerA = "replica-a";
    private const string OwnerB = "replica-b";

    /// <summary>How long a concurrent test waits for its claims, so a hung connection fails the test instead of the run.</summary>
    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(30);

    private readonly List<IAsyncDisposable> _disposables = [];
    private IContainer? _postgres;
    private string _connectionString = null!;
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
        _connectionString = $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
            "Database=postgres;Username=postgres;Password=postgres";
        _db = NpgsqlDataSource.Create(_connectionString);
        await ApplyMigrationsAsync(_db);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
        if (_db is not null) await _db.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task TryClaimAsync_ShouldAdmitExactlyOneClaimant_WhenTwoOwnersRaceForOneCall()
    {
        var a = NewStore(OwnerA);
        var b = NewStore(OwnerB);
        await WarmAsync(a.Claim, b.Claim);

        for (var round = 0; round < 100; round++)
        {
            var call = NewCall();
            var claimA = Guid.NewGuid();
            var claimB = Guid.NewGuid();
            using var barrier = new Barrier(2);

            var raceA = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                return await a.Claim.TryClaimAsync(call, claimA, 1, CancellationToken.None);
            });
            var raceB = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                return await b.Claim.TryClaimAsync(call, claimB, 1, CancellationToken.None);
            });
            var results = await Task.WhenAll(raceA, raceB).WaitAsync(CallBudget);

            results.Count(r => r).Should().Be(1, $"two claimants admitted in round {round}: exactly one may win the call");
            var row = await RowAsync(call);
            row.Should().NotBeNull();
            if (results[0])
                row!.Value.Should().Match<ClaimRow>(r => r.Owner == OwnerA && r.ClaimId == claimA, "the row names the winner");
            else
                row!.Value.Should().Match<ClaimRow>(r => r.Owner == OwnerB && r.ClaimId == claimB, "the row names the winner");
        }

        (a.Counts.Get(AgentAssistCallClaimMetrics.Claimed) + b.Counts.Get(AgentAssistCallClaimMetrics.Claimed)).Should().Be(100);
        (a.Counts.Get(AgentAssistCallClaimMetrics.Held) + b.Counts.Get(AgentAssistCallClaimMetrics.Held)).Should().Be(100);
    }

    [Fact]
    public async Task TryClaimAsync_ShouldReplaceTheClaim_WhenTheSameOwnerClaimsAgainWithAHigherGeneration()
    {
        var a = NewStore(OwnerA);
        var call = NewCall();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        (await a.Claim.TryClaimAsync(call, first, 1, CancellationToken.None)).Should().BeTrue();
        (await a.Claim.TryClaimAsync(call, second, 2, CancellationToken.None))
            .Should().BeTrue("the same owner's later setup replaces its own earlier claim");
        (await RowAsync(call))!.Value.ClaimId.Should().Be(second);

        await a.Claim.ReleaseAsync(call, first, CancellationToken.None);
        (await RowAsync(call)).Should().NotBeNull("claim released by a superseded setup: the old id's release must leave the new claim");
        (await RowAsync(call))!.Value.ClaimId.Should().Be(second);

        await a.Claim.ReleaseAsync(call, second, CancellationToken.None);
        (await RowAsync(call)).Should().BeNull("the new id's release deletes the row");

        a.Counts.Get(AgentAssistCallClaimMetrics.Claimed).Should().Be(2);
        a.Counts.Get(AgentAssistCallClaimMetrics.Released).Should().Be(2);
        a.Counts.Get(AgentAssistCallClaimMetrics.Held).Should().Be(0);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldLeaveTheNewClaim_WhenASupersededSetupReleasesWhileTheNewSetupClaims()
    {
        var a = NewStore(OwnerA);
        await WarmAsync(a.Claim);

        for (var round = 0; round < 20; round++)
        {
            var call = NewCall();
            var old = Guid.NewGuid();
            var current = Guid.NewGuid();
            (await a.Claim.TryClaimAsync(call, old, 1, CancellationToken.None)).Should().BeTrue();
            using var barrier = new Barrier(2);

            var claim = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                return await a.Claim.TryClaimAsync(call, current, 2, CancellationToken.None);
            });
            var release = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                await a.Claim.ReleaseAsync(call, old, CancellationToken.None);
            });
            await Task.WhenAll(claim, release).WaitAsync(CallBudget);

            (await claim).Should().BeTrue();
            var row = await RowAsync(call);
            row.Should().NotBeNull($"claim released by a superseded setup in round {round}: the new setup's claim must stand");
            row!.Value.ClaimId.Should().Be(current, "claim released by a superseded setup");
        }
    }

    [Fact]
    public async Task TryClaimAsync_ShouldRefuseAnEarlierOrEqualGeneration_WhenTheSameOwnersLaterSetupClaimedFirst()
    {
        var a = NewStore(OwnerA);
        var call = NewCall();
        var c2 = Guid.NewGuid();
        var c1 = Guid.NewGuid();
        var equal = Guid.NewGuid();

        (await a.Claim.TryClaimAsync(call, c2, 2, CancellationToken.None)).Should().BeTrue();
        (await a.Claim.TryClaimAsync(call, c1, 1, CancellationToken.None))
            .Should().BeFalse("superseded setup took the claim: generation 1 committed after generation 2 must be refused");
        (await RowAsync(call))!.Value.ClaimId.Should().Be(c2, "superseded setup took the claim");

        await a.Claim.ReleaseAsync(call, c1, CancellationToken.None);
        (await RowAsync(call)).Should().NotBeNull("superseded setup took the claim: its release deleted the later setup's row");
        (await RowAsync(call))!.Value.ClaimId.Should().Be(c2);

        (await a.Claim.TryClaimAsync(call, equal, 2, CancellationToken.None))
            .Should().BeFalse("a claim whose generation is not higher than the held one is refused");
        (await RowAsync(call))!.Value.ClaimId.Should().Be(c2);

        a.Counts.Get(AgentAssistCallClaimMetrics.Held).Should().Be(2);
    }

    [Fact]
    public async Task TryClaimAsync_ShouldLeaveTheClaimWithTheLaterSetup_WhenTheSameOwnersTwoSetupsClaimConcurrently()
    {
        var a = NewStore(OwnerA);
        await WarmAsync(a.Claim);

        for (var round = 0; round < 20; round++)
        {
            var call = NewCall();
            var c1 = Guid.NewGuid();
            var c2 = Guid.NewGuid();
            using var barrier = new Barrier(2);

            var first = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                return await a.Claim.TryClaimAsync(call, c1, 1, CancellationToken.None);
            });
            var second = Task.Run(async () =>
            {
                barrier.SignalAndWait(CallBudget);
                return await a.Claim.TryClaimAsync(call, c2, 2, CancellationToken.None);
            });
            await Task.WhenAll(first, second).WaitAsync(CallBudget);

            (await second).Should().BeTrue("the later setup always gets the call");
            (await RowAsync(call))!.Value.ClaimId.Should().Be(c2, $"superseded setup took the claim in round {round}");
        }
    }

    [Fact]
    public async Task TryClaimAsync_ShouldThrowAndItsReleaseShouldDeleteTheRow_WhenTheClaimCommitsButItsResultIsLost()
    {
        await using var proxy = new HoldingTcpProxy(_postgres!.Hostname, _postgres.GetMappedPublicPort(5432));
        var proxied = NewDataSource(
            $"Host=127.0.0.1;Port={proxy.Port};Database=postgres;Username=postgres;Password=postgres;" +
            "Application Name=claim-ambiguous;Maximum Pool Size=1;Timeout=5");
        var a = NewStore(OwnerA, proxied);
        await WarmAsync(a.Claim);
        var call = NewCall();
        var claimId = Guid.NewGuid();

        proxy.HoldServerReplies();
        var claim = a.Claim.TryClaimAsync(call, claimId, 1, CancellationToken.None).AsTask();
        await WaitUntilAsync(async () => (await RowAsync(call))?.ClaimId == claimId, "the claim's row is visible");
        await TerminateAsync("claim-ambiguous");

        var awaitClaim = async () => await claim.WaitAsync(CallBudget);
        await awaitClaim.Should().ThrowAsync<Exception>("the caller never read the claim's result");
        a.Counts.Get(AgentAssistCallClaimMetrics.Error).Should().Be(1);

        proxy.ForwardServerReplies();
        await a.Claim.ReleaseAsync(call, claimId, CancellationToken.None);
        (await RowAsync(call)).Should().BeNull("the release with the ambiguous claim's id deletes the row it wrote");
        a.Counts.Get(AgentAssistCallClaimMetrics.Released).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ReleaseAsync_ShouldDeleteNothingAndNotThrow_WhenTheClaimIdNeverCommitted()
    {
        var a = NewStore(OwnerA);
        var held = NewCall();
        var claimId = Guid.NewGuid();
        (await a.Claim.TryClaimAsync(held, claimId, 1, CancellationToken.None)).Should().BeTrue();

        var releaseOther = async () => await a.Claim.ReleaseAsync(held, Guid.NewGuid(), CancellationToken.None);
        var releaseMissing = async () => await a.Claim.ReleaseAsync(NewCall(), Guid.NewGuid(), CancellationToken.None);

        await releaseOther.Should().NotThrowAsync();
        await releaseMissing.Should().NotThrowAsync();
        (await RowAsync(held))!.Value.ClaimId.Should().Be(claimId, "a release of an id that never committed deletes nothing");
        a.Counts.Get(AgentAssistCallClaimMetrics.ReleaseError).Should().Be(0);
    }

    [Fact]
    public async Task TryClaimAsync_ShouldRefuseAnotherOwner_WhileTheClaimIsYoungerThan24Hours_AndAdmitItOnceOlder()
    {
        var a = NewStore(OwnerA);
        var b = NewStore(OwnerB);
        var call = NewCall();
        var claimA = Guid.NewGuid();
        var claimB = Guid.NewGuid();
        (await a.Claim.TryClaimAsync(call, claimA, 1, CancellationToken.None)).Should().BeTrue();

        (await b.Claim.TryClaimAsync(call, claimB, 99, CancellationToken.None))
            .Should().BeFalse("another owner's claim stands, whatever the generation");
        await AgeAsync(call, TimeSpan.FromHours(23));
        (await b.Claim.TryClaimAsync(call, claimB, 99, CancellationToken.None))
            .Should().BeFalse("a claim younger than 24 h is never taken over");
        (await RowAsync(call))!.Value.Should().Match<ClaimRow>(r => r.Owner == OwnerA && r.ClaimId == claimA);

        await AgeAsync(call, TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        (await b.Claim.TryClaimAsync(call, claimB, 1, CancellationToken.None))
            .Should().BeTrue("a claim older than the abandon age is taken over");
        (await RowAsync(call))!.Value.Should().Match<ClaimRow>(r => r.Owner == OwnerB && r.ClaimId == claimB);

        b.Counts.Get(AgentAssistCallClaimMetrics.Held).Should().Be(2);
        b.Counts.Get(AgentAssistCallClaimMetrics.Claimed).Should().Be(1);
        a.Counts.Get(AgentAssistCallClaimMetrics.Claimed).Should().Be(1);
    }

    /// <summary>
    /// The one-session promise holds while the holder's claim is younger than 24 h. The claim
    /// is not renewed, so a session that runs past 24 h loses its call to another owner that handles a connect for it.
    /// This test pins that documented limit: a change that adds renewal turns it red on purpose.
    /// </summary>
    [Fact]
    public async Task TryClaimAsync_ShouldHandTheCallToAnotherOwner_WhenTheClaimOfAStillRunningSessionIsOlderThan24Hours()
    {
        var a = NewStore(OwnerA);
        var b = NewStore(OwnerB);
        var call = NewCall();
        var running = Guid.NewGuid();
        var other = Guid.NewGuid();
        (await a.Claim.TryClaimAsync(call, running, 1, CancellationToken.None)).Should().BeTrue();

        // Owner A's session is still running: it has not released. Its claim ages past the bound.
        await AgeAsync(call, TimeSpan.FromHours(25));

        (await b.Claim.TryClaimAsync(call, other, 1, CancellationToken.None))
            .Should().BeTrue("the bound is 24 h: an unreleased claim older than that is taken over, by design");
        await a.Claim.ReleaseAsync(call, running, CancellationToken.None);
        (await RowAsync(call))!.Value.Should().Match<ClaimRow>(r => r.Owner == OwnerB && r.ClaimId == other,
            "the first owner's late release names its own claim id and leaves the new holder's row");
    }

    [Fact]
    public async Task TryClaimAsync_ShouldSweepOnlyRowsOlderThan24Hours_AtMostOnceAnHour()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        var sweeper = NewStore(OwnerA, time: time);
        var other = NewStore(OwnerB);
        var old = NewCall();
        var young = NewCall();
        (await other.Claim.TryClaimAsync(old, Guid.NewGuid(), 1, CancellationToken.None)).Should().BeTrue();
        (await other.Claim.TryClaimAsync(young, Guid.NewGuid(), 1, CancellationToken.None)).Should().BeTrue();

        // The sweeper's first claim sweeps (nothing is old yet); its next claims within the hour do not.
        (await sweeper.Claim.TryClaimAsync(NewCall(), Guid.NewGuid(), 1, CancellationToken.None)).Should().BeTrue();
        await AgeAsync(old, TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        await AgeAsync(young, TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));

        time.Advance(TimeSpan.FromMinutes(59));
        var mine = NewCall();
        (await sweeper.Claim.TryClaimAsync(mine, Guid.NewGuid(), 1, CancellationToken.None)).Should().BeTrue();
        (await RowAsync(old)).Should().NotBeNull("the store sweeps at most once an hour");

        time.Advance(TimeSpan.FromMinutes(1));
        (await sweeper.Claim.TryClaimAsync(NewCall(), Guid.NewGuid(), 1, CancellationToken.None)).Should().BeTrue();
        (await RowAsync(old)).Should().BeNull("the hourly sweep deletes a row older than 24 h");
        (await RowAsync(young)).Should().NotBeNull("the sweep leaves a row younger than 24 h");
        (await RowAsync(mine)).Should().NotBeNull();
    }

    [Fact]
    public async Task TryClaimAsync_ShouldThrow_AndReleaseAsyncShouldLogAndCountWithoutThrowing_WhenTheDatabaseIsUnreachable()
    {
        var unreachable = NewDataSource(
            $"Host=127.0.0.1;Port={UnusedPort()};Database=postgres;Username=postgres;Password=postgres;Timeout=2");
        var a = NewStore(OwnerA, unreachable);
        var call = NewCall();
        var claimId = Guid.NewGuid();

        var claim = async () => await a.Claim.TryClaimAsync(call, claimId, 1, CancellationToken.None);
        await claim.Should().ThrowAsync<Exception>("an unreachable store fails the claim, and the engine fails closed");
        a.Counts.Get(AgentAssistCallClaimMetrics.Error).Should().Be(1);

        var release = async () => await a.Claim.ReleaseAsync(call, claimId, CancellationToken.None);
        await release.Should().NotThrowAsync("a failed release never stops the teardown");
        a.Counts.Get(AgentAssistCallClaimMetrics.ReleaseError).Should().Be(1);
        a.Counts.Get(AgentAssistCallClaimMetrics.Released).Should().Be(0);
        a.Logs.Should().Contain(l => l.Level == LogLevel.Warning && l.Exception != null, "a failed release is logged");
    }

    [Fact]
    public async Task ReleaseAsync_ShouldCountReleaseErrorWithoutThrowing_WhenItsBoundedTokenIsCancelled()
    {
        var a = NewStore(OwnerA);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var release = async () => await a.Claim.ReleaseAsync(NewCall(), Guid.NewGuid(), cancelled.Token);

        await release.Should().NotThrowAsync();
        a.Counts.Get(AgentAssistCallClaimMetrics.ReleaseError).Should().Be(1);
    }

    [Fact]
    public async Task TryClaimAsync_ShouldKeyTheRowByServerIdAndLinkedId()
    {
        var a = NewStore(OwnerA);
        var call = new AgentAssistCallKey("node-" + Guid.NewGuid().ToString("N"), "1731830400.42");

        (await a.Claim.TryClaimAsync(call, Guid.NewGuid(), 7, CancellationToken.None)).Should().BeTrue();

        PostgresAgentAssistCallClaim.CallKey(call).Should().Be(call.ServerId + "/" + call.LinkedId);
        await using var cmd = _db.CreateCommand("SELECT generation FROM agent_assist_call_claims WHERE call_key = @k");
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = call.ServerId + "/1731830400.42" });
        (await cmd.ExecuteScalarAsync()).Should().Be(7L);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TryClaimAsync_ShouldRefuseABlankLinkedId_BeforeAnySql(string linkedId)
    {
        // An unreachable store: any SQL would fail with a connection error, not with the argument check.
        var unreachable = NewDataSource(
            $"Host=127.0.0.1;Port={UnusedPort()};Database=postgres;Username=postgres;Password=postgres;Timeout=2");
        var a = NewStore(OwnerA, unreachable);
        var call = new AgentAssistCallKey("node-1", linkedId);

        var claim = async () => await a.Claim.TryClaimAsync(call, Guid.NewGuid(), 1, CancellationToken.None);
        var release = async () => await a.Claim.ReleaseAsync(call, Guid.NewGuid(), CancellationToken.None);

        await claim.Should().ThrowExactlyAsync<ArgumentException>();
        await release.Should().NotThrowAsync();
        a.Counts.Get(AgentAssistCallClaimMetrics.ReleaseError).Should().Be(0, "a blank key makes no release statement");
    }

    // ---------------------------------------------------------------- helpers

    private readonly record struct ClaimRow(Guid ClaimId, string Owner, long Generation);

    private sealed record Store(PostgresAgentAssistCallClaim Claim, OutcomeCounts Counts, ConcurrentQueue<LogEntry> Logs);

    private Store NewStore(string owner, NpgsqlDataSource? dataSource = null, TimeProvider? time = null)
    {
        var metrics = new AgentAssistCallClaimMetrics();
        var counts = new OutcomeCounts(metrics);
        var logs = new ConcurrentQueue<LogEntry>();
        _disposables.Add(counts);
        var claim = new PostgresAgentAssistCallClaim(
            dataSource ?? NewDataSource(_connectionString), owner, metrics, new ListLogger(logs), time);
        return new Store(claim, counts, logs);
    }

    private NpgsqlDataSource NewDataSource(string connectionString)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        _disposables.Add(dataSource);
        return dataSource;
    }

    private static AgentAssistCallKey NewCall() => new("node-1", Guid.NewGuid().ToString("N"));

    /// <summary>Opens each store's pooled connection with a release of an unrelated key, so a race starts warm.</summary>
    private static async Task WarmAsync(params PostgresAgentAssistCallClaim[] claims)
    {
        foreach (var claim in claims)
            await claim.ReleaseAsync(NewCall(), Guid.NewGuid(), CancellationToken.None);
    }

    private async Task<ClaimRow?> RowAsync(AgentAssistCallKey call)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT claim_id, owner, generation FROM agent_assist_call_claims WHERE call_key = @k");
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = call.ServerId + "/" + call.LinkedId });
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;
        return new ClaimRow(reader.GetGuid(0), reader.GetString(1), reader.GetInt64(2));
    }

    private async Task AgeAsync(AgentAssistCallKey call, TimeSpan age)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE agent_assist_call_claims SET claimed_at = statement_timestamp() - @age WHERE call_key = @k");
        cmd.Parameters.Add(new NpgsqlParameter("age", NpgsqlDbType.Interval) { Value = age });
        cmd.Parameters.Add(new NpgsqlParameter("k", NpgsqlDbType.Text) { Value = call.ServerId + "/" + call.LinkedId });
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task TerminateAsync(string applicationName)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT count(*) FROM (SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @a) t");
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Text) { Value = applicationName });
        ((long)(await cmd.ExecuteScalarAsync())!).Should().Be(1, "exactly the claimant's backend is terminated");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + CallBudget;
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, $"{what} within {CallBudget}");
            await Task.Delay(20); // fence-allow: LOOP-DRIVER — poll pacing until another session sees the committed row, bounded by CallBudget
        }
    }

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // As DispositionDispatchPostgresFixture: every embedded Migrations/*.sql of Storage.Postgres, in ordinal order.
    internal static async Task ApplyMigrationsAsync(NpgsqlDataSource dataSource)
    {
        var assembly = typeof(Verbara.Platform.Storage.Postgres.ServiceCollectionExtensions).Assembly;
        const string prefix = "Verbara.Platform.Storage.Postgres.Migrations.";
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        resources.Should().NotBeEmpty();

        await using var conn = await dataSource.OpenConnectionAsync();
        foreach (var resourceName in resources)
        {
            await using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = await reader.ReadToEndAsync();
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Counts <c>platform.agentassist.call_claim</c> by <c>outcome</c>, from one metrics instance only.</summary>
    private sealed class OutcomeCounts : IAsyncDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly AgentAssistCallClaimMetrics _metrics;

        public OutcomeCounts(AgentAssistCallClaimMetrics metrics)
        {
            _metrics = metrics;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter)
                    && instrument.Name == AgentAssistCallClaimMetrics.CallClaimInstrument)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "outcome" && tag.Value is string outcome)
                        _counts.AddOrUpdate(outcome, value, (_, sum) => sum + value);
            });
            _listener.Start();
        }

        public long Get(string outcome) => _counts.GetValueOrDefault(outcome);

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            _metrics.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger(ConcurrentQueue<LogEntry> entries) : ILogger<PostgresAgentAssistCallClaim>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// A TCP proxy in front of the container's Postgres that can stop forwarding the server's replies, so a statement
    /// commits on the server while its caller never reads the result. When the server side closes (the backend was
    /// terminated), the proxy closes the caller's side: the caller sees the connection lost after the commit.
    /// </summary>
    private sealed class HoldingTcpProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _serverHost;
        private readonly int _serverPort;
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<TcpClient> _sockets = [];
        private readonly ConcurrentQueue<Exception> _errors = new();
        private readonly Task _accept;
        private volatile bool _hold;

        public HoldingTcpProxy(string serverHost, int serverPort)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
            _listener.Start();
            _accept = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void HoldServerReplies() => _hold = true;

        public void ForwardServerReplies() => _hold = false;

        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var server = new TcpClient();
                    await server.ConnectAsync(_serverHost, _serverPort, _stop.Token);
                    _sockets.Add(client);
                    _sockets.Add(server);
                    _ = PumpAsync(client, server, holdable: false);
                    _ = PumpAsync(server, client, holdable: true);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // The proxy is being disposed: the listener stopped.
                _errors.Enqueue(ex);
            }
        }

        private async Task PumpAsync(TcpClient from, TcpClient to, bool holdable)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                var input = from.GetStream();
                var output = to.GetStream();
                while (true)
                {
                    var read = await input.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                        break;
                    if (holdable && _hold)
                        continue;
                    await output.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // One side closed or the proxy is being disposed; the finally closes both sides.
                _errors.Enqueue(ex);
            }
            finally
            {
                from.Close();
                to.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            foreach (var socket in _sockets)
                socket.Dispose();
            await _accept;
            _stop.Dispose();
        }
    }
}
