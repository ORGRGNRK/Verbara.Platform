using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.3 (design D6): the Agent Assist feature state over a Testcontainers Postgres
/// with the real embedded <c>Storage.Postgres</c> migrations, read and written by two toggle instances standing for two
/// replicas. The administrator's read and a session's read go to the row directly, whatever the engine path has
/// cached; the engine path sees a write once its cache window has passed; a database error answers the last known
/// state, counted; seeding leaves one row and never overwrites an administrator's write.
/// </summary>
/// <remarks>
/// Each test runs in a database of its own, created from a migrated template, so dropping one (a database error) or
/// writing its one row never affects another test.
/// </remarks>
public sealed class PostgresAgentAssistFeatureToggleLiveDbTests : IAsyncLifetime
{
    private const string Template = "toggle_template";
    private const string KeyOne = "dg-key-one-5b1e0c";
    private const string KeyTwo = "dg-key-two-9f47aa";
    private const string KeyThree = "dg-key-three-27d3";
    private const string Admin = "0651f84b6f284a1c9b8d658330fa8310";

    private static readonly TimeSpan CallBudget = TimeSpan.FromSeconds(30);

    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly AgentAssistCredentialsProtector _protector = new(new EphemeralDataProtectionProvider());
    private IContainer? _postgres;
    private string _server = null!;
    private NpgsqlDataSource _admin = null!;

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
        _server = $"Host={_postgres.Hostname};Port={_postgres.GetMappedPublicPort(5432)};" +
            "Username=postgres;Password=postgres";
        _admin = NpgsqlDataSource.Create(_server + ";Database=postgres");

        await ExecuteAsync(_admin, $"CREATE DATABASE {Template}");
        // Pooling off: a template database must have no open connection when it is copied.
        await using (var template = NpgsqlDataSource.Create(_server + $";Database={Template};Pooling=false"))
            await ApplyMigrationsAsync(template);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
        if (_admin is not null) await _admin.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    // ---------------------------------------------------------------- direct reads

    [Fact]
    public async Task ReadAsync_ShouldAnswerTheNewState_WhenAnotherReplicaWroteItJustBefore()
    {
        var db = await NewDatabaseAsync();
        var a = NewToggle(db, Off());
        var b = NewToggle(db, Off());
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeFalse("replica A's engine cache is warm with the seed");

        await b.Toggle.SetAsync(Deepgram(KeyTwo));

        var read = await a.Toggle.ReadAsync();
        read.Enabled.Should().BeTrue("a GET through replica A right after a PUT through replica B answers the new state");
        read.Provider.Should().Be("deepgram");
        read.UpdatedBy.Should().Be(Admin);
        ApiKey(read).Should().Be(KeyTwo);
    }

    [Fact]
    public async Task ReadForSessionAsync_ShouldAnswerTheNewKey_WhenAnotherReplicaRotatedItWhileTheEngineCacheIsWarm()
    {
        var db = await NewDatabaseAsync();
        var time = new FakeTimeProvider();
        var a = NewToggle(db, DeepgramConfig(KeyOne), time);
        var b = NewToggle(db, DeepgramConfig(KeyOne));
        ApiKey(await a.Toggle.GetCurrentAsync()).Should().Be(KeyOne, "replica A's engine cache read K1");

        await b.Toggle.SetAsync(Deepgram(KeyTwo));
        ApiKey(await a.Toggle.GetCurrentAsync())
            .Should().Be(KeyOne, "no time passed, so replica A's engine cache is still warm with K1");

        ApiKey(await a.Toggle.ReadForSessionAsync())
            .Should().Be(KeyTwo, "a session whose read starts after the PUT uses the PUT's key, whatever the cache holds");
        a.Counts.Get(AgentAssistToggleMetrics.RecognizerPath).Should().Be(0);
    }

    [Fact]
    public async Task ReadForSessionAsync_ShouldAnswerOff_WhenAnotherReplicaSwitchedItOffWhileTheEngineCacheIsWarm()
    {
        var db = await NewDatabaseAsync();
        var time = new FakeTimeProvider();
        var a = NewToggle(db, DeepgramConfig(KeyOne), time);
        var b = NewToggle(db, DeepgramConfig(KeyOne));
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeTrue();

        await b.Toggle.SetAsync(new AgentAssistFeatureState(false, null, null, null, Admin));
        (await a.Toggle.GetCurrentAsync()).Enabled
            .Should().BeTrue("no time passed, so replica A's engine cache is still warm with the on state");

        var session = await a.Toggle.ReadForSessionAsync();
        session.Enabled.Should().BeFalse("a switch to off applies to the next session's read on any replica");
        session.ProtectedCredentials.Should().BeNull();
    }

    // ---------------------------------------------------------------- engine-path cache

    [Fact]
    public async Task GetCurrentAsync_ShouldSeeAnotherReplicasWrite_OnceTheCacheWindowHasPassed()
    {
        var db = await NewDatabaseAsync();
        var time = new FakeTimeProvider();
        var a = NewToggle(db, Off(), time);
        var b = NewToggle(db, Off());
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeFalse();

        await b.Toggle.SetAsync(Deepgram(KeyTwo));

        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeFalse("within the window the engine path answers its cache");
        time.Advance(PostgresAgentAssistFeatureToggle.CacheWindow - TimeSpan.FromMilliseconds(1));
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));
        var engine = await a.Toggle.GetCurrentAsync();
        engine.Enabled.Should().BeTrue("the engine path sees the write within the 5 s cache window");
        ApiKey(engine).Should().Be(KeyTwo);
    }

    [Fact]
    public async Task GetCurrentAsync_ShouldSeeItsOwnWrite_AtOnce()
    {
        var db = await NewDatabaseAsync();
        var a = NewToggle(db, Off(), new FakeTimeProvider());
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeFalse();

        var stored = await a.Toggle.SetAsync(Deepgram(KeyTwo) with { Provider = " DeepGram " });

        stored.Provider.Should().Be("deepgram", "the stored provider is trimmed and lowercased");
        (await a.Toggle.GetCurrentAsync()).Should().BeEquivalentTo(stored);
    }

    // ---------------------------------------------------------------- database errors

    [Fact]
    public async Task GetCurrentAsync_ShouldAnswerTheLastKnownState_AndCount_WhenTheDatabaseFails()
    {
        var db = await NewDatabaseAsync();
        var time = new FakeTimeProvider();
        var a = NewToggle(db, DeepgramConfig(KeyOne), time);
        ApiKey(await a.Toggle.GetCurrentAsync()).Should().Be(KeyOne);

        await DropDatabaseAsync(db);
        time.Advance(PostgresAgentAssistFeatureToggle.CacheWindow);

        var engine = await a.Toggle.GetCurrentAsync();
        engine.Enabled.Should().BeTrue("the engine uses the last known state instead of failing the call");
        ApiKey(engine).Should().Be(KeyOne);
        a.Counts.Get(AgentAssistToggleMetrics.EnginePath).Should().Be(1);
        a.Logs.Should().ContainSingle(l => l.Level == LogLevel.Warning && l.Exception != null, "the failure is logged");

        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeTrue();
        a.Counts.Get(AgentAssistToggleMetrics.EnginePath)
            .Should().Be(1, "within the window after a failure the engine path does not try the database again");
        time.Advance(PostgresAgentAssistFeatureToggle.CacheWindow);
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeTrue();
        a.Counts.Get(AgentAssistToggleMetrics.EnginePath).Should().Be(2, "once the window has passed it tries again");
        AssertNoCredentialLogged(a);
    }

    [Fact]
    public async Task ReadForSessionAsync_ShouldAnswerTheLastKnownState_AndCount_WhenTheDatabaseFails()
    {
        var db = await NewDatabaseAsync();
        var a = NewToggle(db, Off());
        await a.Toggle.SetAsync(Deepgram(KeyTwo));

        await DropDatabaseAsync(db);

        var session = await a.Toggle.ReadForSessionAsync();
        session.Enabled.Should().BeTrue();
        ApiKey(session).Should().Be(KeyTwo, "only while the database fails does a session use the last known state");
        a.Counts.Get(AgentAssistToggleMetrics.RecognizerPath).Should().Be(1);
        a.Counts.Get(AgentAssistToggleMetrics.EnginePath).Should().Be(0);
        a.Logs.Should().ContainSingle(l => l.Level == LogLevel.Warning && l.Exception != null);

        var admin = async () => await a.Toggle.ReadAsync();
        await admin.Should().ThrowAsync<Exception>("the administrator's read never answers a stale state");
        AssertNoCredentialLogged(a);
    }

    [Fact]
    public async Task GetCurrentAsyncAndReadForSessionAsync_ShouldAnswerDisabled_AndCount_WhenTheDatabaseWasNeverReached()
    {
        var unreachable = $"Host=127.0.0.1;Port={UnusedPort()};Database=postgres;Username=postgres;Password=postgres;Timeout=2";
        var a = NewToggle(unreachable, DeepgramConfig(KeyOne));

        (await a.Toggle.GetCurrentAsync()).Should().Be(AgentAssistFeatureState.Disabled,
            "with no state ever read, the engine creates no snoop rather than trusting configuration over the store");
        (await a.Toggle.ReadForSessionAsync()).Should().Be(AgentAssistFeatureState.Disabled);
        a.Counts.Get(AgentAssistToggleMetrics.EnginePath).Should().Be(1);
        a.Counts.Get(AgentAssistToggleMetrics.RecognizerPath).Should().Be(1);
    }

    // ---------------------------------------------------------------- seeding

    [Fact]
    public async Task ReadAsync_ShouldLeaveOneRow_WhenTwoReplicasSeedAtOnce()
    {
        var db = await NewDatabaseAsync();
        for (var round = 0; round < 20; round++)
        {
            await ExecuteAsync(db.Source, "DELETE FROM agent_assist_feature_state");
            var a = NewToggle(db, DeepgramConfig(KeyOne));
            var b = NewToggle(db, DeepgramConfig(KeyThree));
            using var barrier = new Barrier(2);

            var seeds = await Task.WhenAll(
                Task.Run(async () => { barrier.SignalAndWait(CallBudget); return await a.Toggle.ReadAsync(); }),
                Task.Run(async () => { barrier.SignalAndWait(CallBudget); return await b.Toggle.ReadAsync(); }))
                .WaitAsync(CallBudget);

            (await CountRowsAsync(db)).Should().Be(1, $"two replicas seeding at once leave one row (round {round})");
            seeds.Should().AllSatisfy(s =>
            {
                s.Enabled.Should().BeTrue();
                s.UpdatedBy.Should().Be(AgentAssistFeatureSeed.UpdatedBy);
            });
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldNeverOverwriteAnAdministratorsWrite_WhenAReplicaSeedsAtTheSameTime()
    {
        var db = await NewDatabaseAsync();
        for (var round = 0; round < 20; round++)
        {
            await ExecuteAsync(db.Source, "DELETE FROM agent_assist_feature_state");
            var writer = NewToggle(db, Off());
            var seeder = NewToggle(db, DeepgramConfig(KeyOne));
            using var barrier = new Barrier(2);

            await Task.WhenAll(
                Task.Run(async () => { barrier.SignalAndWait(CallBudget); await writer.Toggle.SetAsync(Deepgram(KeyTwo)); }),
                Task.Run(async () => { barrier.SignalAndWait(CallBudget); await seeder.Toggle.ReadAsync(); }))
                .WaitAsync(CallBudget);

            var stored = await NewToggle(db, Off()).Toggle.ReadAsync();
            stored.UpdatedBy.Should().Be(Admin, $"configuration never overwrites an administrator's write (round {round})");
            ApiKey(stored).Should().Be(KeyTwo);
        }
    }

    [Fact]
    public async Task ReadAsync_ShouldApplyAChangedConfiguration_OnlyUntilAnAdministratorWritesTheRow()
    {
        var db = await NewDatabaseAsync();
        var first = await NewToggle(db, DeepgramConfig(KeyOne)).Toggle.ReadAsync();
        first.UpdatedBy.Should().Be(AgentAssistFeatureSeed.UpdatedBy);
        ApiKey(first).Should().Be(KeyOne);

        var restarted = await NewToggle(db, DeepgramConfig(KeyThree, "wss://stt.example.test")).Toggle.ReadAsync();
        ApiKey(restarted).Should().Be(KeyThree, "a row no administrator wrote takes the changed configuration at restart");
        _protector.Unprotect(restarted.ProtectedCredentials!)["endpoint"].Should().Be("wss://stt.example.test");

        await NewToggle(db, DeepgramConfig(KeyThree)).Toggle.SetAsync(Deepgram(KeyTwo));
        var afterAdmin = await NewToggle(db, Off()).Toggle.ReadAsync();
        afterAdmin.Enabled.Should().BeTrue("an administrator's row survives a restart with a changed configuration");
        afterAdmin.UpdatedBy.Should().Be(Admin);
        ApiKey(afterAdmin).Should().Be(KeyTwo);
    }

    [Fact]
    public async Task SetAsync_ShouldStoreOnlyEncryptedCredentials()
    {
        var db = await NewDatabaseAsync();
        var a = NewToggle(db, Off());
        (await a.Toggle.ReadAsync()).Should().Match<AgentAssistFeatureState>(s =>
            !s.Enabled && s.Provider == null && s.ProtectedCredentials == null && s.UpdatedBy == AgentAssistFeatureSeed.UpdatedBy);

        await a.Toggle.SetAsync(Deepgram(KeyTwo));

        await using var cmd = db.Source.CreateCommand("SELECT protected_credentials::text FROM agent_assist_feature_state");
        var stored = (string?)await cmd.ExecuteScalarAsync();
        stored.Should().NotBeNull().And.Contain("\"apiKey\"").And.NotContain(KeyTwo, "credentials are never stored in plain text");
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Replica(PostgresAgentAssistFeatureToggle Toggle, StaleCounts Counts, ConcurrentQueue<LogEntry> Logs);

    private Replica NewToggle(TestDatabase db, AgentAssistOptions options, TimeProvider? time = null) =>
        NewToggle(db.ConnectionString, options, time);

    private Replica NewToggle(string connectionString, AgentAssistOptions options, TimeProvider? time = null)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        _disposables.Add(dataSource);
        var metrics = new AgentAssistToggleMetrics();
        var counts = new StaleCounts(metrics);
        _disposables.Add(counts);
        var logs = new ConcurrentQueue<LogEntry>();
        var toggle = new PostgresAgentAssistFeatureToggle(
            dataSource, Options.Create(options), _protector, metrics, new ListLogger(logs), time);
        return new Replica(toggle, counts, logs);
    }

    private static AgentAssistOptions Off() => new() { Enabled = false };

    private static AgentAssistOptions DeepgramConfig(string apiKey, string? endpoint = null) => new()
    {
        Enabled = true,
        Provider = "deepgram",
        Deepgram = new AgentAssistDeepgramOptions { ApiKey = apiKey, Endpoint = endpoint },
    };

    private AgentAssistFeatureState Deepgram(string apiKey) => new(
        Enabled: true,
        Provider: "deepgram",
        ProtectedCredentials: _protector.Protect(new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = apiKey }),
        UpdatedAt: DateTimeOffset.UtcNow,
        UpdatedBy: Admin);

    private string ApiKey(AgentAssistFeatureState state)
    {
        state.ProtectedCredentials.Should().NotBeNull();
        return _protector.Unprotect(state.ProtectedCredentials!)["apiKey"];
    }

    private static void AssertNoCredentialLogged(Replica replica) =>
        replica.Logs.Should().NotContain(
            l => l.Message.Contains(KeyOne) || l.Message.Contains(KeyTwo) || l.Message.Contains(KeyThree)
                 || (l.Exception != null && l.Exception.ToString().Contains("dg-key")),
            "no credential value reaches a log line");

    /// <summary>A test's own database: its name, a connection string with the password, and a data source on it.</summary>
    private sealed record TestDatabase(string Name, string ConnectionString, NpgsqlDataSource Source);

    /// <summary>A database of its own for one test, copied from the migrated template.</summary>
    private async Task<TestDatabase> NewDatabaseAsync()
    {
        var name = "t_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(_admin, $"CREATE DATABASE {name} TEMPLATE {Template}");
        var connectionString = _server + $";Database={name}";
        var dataSource = NpgsqlDataSource.Create(connectionString);
        _disposables.Add(dataSource);
        return new TestDatabase(name, connectionString, dataSource);
    }

    /// <summary>Drops the test's database under its open connections: every later statement on it fails.</summary>
    private Task DropDatabaseAsync(TestDatabase db) => ExecuteAsync(_admin, $"DROP DATABASE {db.Name} WITH (FORCE)");

    private static async Task<long> CountRowsAsync(TestDatabase db)
    {
        await using var cmd = db.Source.CreateCommand("SELECT count(*) FROM agent_assist_feature_state");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
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
    private static async Task ApplyMigrationsAsync(NpgsqlDataSource dataSource)
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

    /// <summary>Counts <c>platform.agentassist.toggle_stale</c> by <c>path</c>, from one metrics instance only.</summary>
    private sealed class StaleCounts : IAsyncDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly AgentAssistToggleMetrics _metrics;

        public StaleCounts(AgentAssistToggleMetrics metrics)
        {
            _metrics = metrics;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, metrics.Meter)
                    && instrument.Name == AgentAssistToggleMetrics.ToggleStaleInstrument)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "path" && tag.Value is string path)
                        _counts.AddOrUpdate(path, value, (_, sum) => sum + value);
            });
            _listener.Start();
        }

        public long Get(string path) => _counts.GetValueOrDefault(path);

        public ValueTask DisposeAsync()
        {
            _listener.Dispose();
            _metrics.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class ListLogger(ConcurrentQueue<LogEntry> entries) : ILogger<PostgresAgentAssistFeatureToggle>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
