using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core.Configuration;
using Verbara.Platform.Identity.DependencyInjection;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.2, the session half of B.3's warm-cache scenarios (design D5, D6):
/// two replicas over one Testcontainers Postgres with the real migrations, each with its own Data Protection stack
/// over the one shared keyring table, as the host configures it. A key saved through one replica is decrypted by the
/// other's session; a rotation or a switch to off saved through one replica applies to the next session on the other,
/// whatever that replica's engine path has cached.
/// </summary>
public sealed class AgentAssistRecognizerSessionLiveDbTests : IAsyncLifetime
{
    private const string KeyOne = "dg-live-key-one-3a90";
    private const string KeyTwo = "dg-live-key-two-c7e1";
    private const string Admin = "0651f84b6f284a1c9b8d658330fa8310";

    private readonly List<IAsyncDisposable> _disposables = [];
    private IContainer? _postgres;
    private string _connectionString = null!;

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
            "Username=postgres;Password=postgres;Database=postgres";
        await using var migrations = NpgsqlDataSource.Create(_connectionString);
        await ApplyMigrationsAsync(migrations);
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
            await disposable.DisposeAsync();
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Session_ShouldDecryptAKeySavedThroughAnotherReplica_WhenBothProtectorsShareTheKeyringTable()
    {
        var a = NewReplica();
        var b = NewReplica();
        a.Protector.Should().NotBeSameAs(b.Protector);

        await b.Toggle.SetAsync(Deepgram(b, KeyOne));
        var factory = new RecordingRecognizerFactory();
        await DrainAsync(a.NewWrapper(a.NewSession(factory)));

        factory.Calls.Should().ContainSingle().Which.Settings.ApiKey
            .Should().Be(KeyOne, "replica A decrypts with the key replica B created in the shared keyring table");
        (await CountKeysAsync()).Should().BeGreaterThan(0, "the keyring lives in the database, not in either process");
        a.Unavailable.Total.Should().Be(0);
    }

    [Fact]
    public async Task Session_ShouldBuildBothRecognizersWithTheNewKey_WhenAnotherReplicaRotatedItWhileTheEngineCacheIsWarm()
    {
        var a = NewReplica();
        var b = NewReplica();
        await b.Toggle.SetAsync(Deepgram(b, KeyOne));
        var engine = await a.Toggle.GetCurrentAsync();
        engine.Enabled.Should().BeTrue("replica A's engine cache is warm");

        await b.Toggle.SetAsync(Deepgram(b, KeyTwo));
        a.Protector.Unprotect((await a.Toggle.GetCurrentAsync()).ProtectedCredentials!)["apiKey"]
            .Should().Be(KeyOne, "replica A's engine path still answers from its cache");

        var factory = new RecordingRecognizerFactory();
        var session = a.NewSession(factory);
        await Task.WhenAll(DrainAsync(a.NewWrapper(session)), DrainAsync(a.NewWrapper(session)));

        factory.Calls.Should().HaveCount(2).And.AllSatisfy(c => c.Settings.ApiKey.Should().Be(KeyTwo,
            "a session whose read starts after the PUT is transcribed with the PUT's key on both streams"));
    }

    [Fact]
    public async Task Session_ShouldCountDisabledAndSendNoAudio_WhenAnotherReplicaSwitchedItOffWhileTheEngineCacheIsWarm()
    {
        var a = NewReplica();
        var b = NewReplica();
        await b.Toggle.SetAsync(Deepgram(b, KeyOne));
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeTrue("replica A's engine cache is warm");

        await b.Toggle.SetAsync(new AgentAssistFeatureState(false, null, null, null, Admin));
        (await a.Toggle.GetCurrentAsync()).Enabled.Should().BeTrue("the engine path may still create the snoop");

        var factory = new RecordingRecognizerFactory();
        var session = a.NewSession(factory);
        await Task.WhenAll(DrainAsync(a.NewWrapper(session)), DrainAsync(a.NewWrapper(session)));

        factory.Calls.Should().BeEmpty("no recognizer is built, so no audio goes to any provider");
        a.Unavailable.Get(AgentAssistRecognizerMetrics.Disabled).Should().Be(1, "one count for the session");
        a.Logs.RecordsContaining(KeyOne).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>One replica: its own Data Protection stack over the shared keyring table, its own toggle.</summary>
    private sealed class Replica(
        PostgresAgentAssistFeatureToggle toggle,
        AgentAssistCredentialsProtector protector,
        AgentAssistRecognizerMetrics metrics,
        UnavailableCounts unavailable,
        LogRecordCapture logs,
        ILoggerFactory loggerFactory)
    {
        public PostgresAgentAssistFeatureToggle Toggle => toggle;

        public AgentAssistCredentialsProtector Protector => protector;

        public UnavailableCounts Unavailable => unavailable;

        public LogRecordCapture Logs => logs;

        public AgentAssistRecognizerSession NewSession(RecordingRecognizerFactory factory) => new(
            toggle, protector,
            Options.Create(new AgentAssistOptions { Deepgram = new AgentAssistDeepgramOptions { Language = "es" } }),
            factory, metrics, loggerFactory.CreateLogger<AgentAssistRecognizerSession>());

        public ToggleBackedSpeechRecognizer NewWrapper(AgentAssistRecognizerSession session) =>
            new(session, loggerFactory.CreateLogger<ToggleBackedSpeechRecognizer>());
    }

    private Replica NewReplica()
    {
        var dataSource = NpgsqlDataSource.Create(_connectionString);
        _disposables.Add(dataSource);
        var logs = new LogRecordCapture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddPlatformDataProtection(o =>
        {
            o.ApplicationName = "Verbara.Platform";
            o.UsePostgres(dataSource);
        });
        services.AddSingleton<AgentAssistCredentialsProtector>();
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);

        var protector = provider.GetRequiredService<AgentAssistCredentialsProtector>();
        var toggleMetrics = new AgentAssistToggleMetrics();
        var metrics = new AgentAssistRecognizerMetrics();
        var unavailable = new UnavailableCounts(metrics);
        _disposables.Add(new Disposer(() =>
        {
            unavailable.Dispose();
            metrics.Dispose();
            toggleMetrics.Dispose();
        }));
        var toggle = new PostgresAgentAssistFeatureToggle(
            dataSource, Options.Create(new AgentAssistOptions()), protector, toggleMetrics,
            NullLogger<PostgresAgentAssistFeatureToggle>.Instance);
        return new Replica(toggle, protector, metrics, unavailable, logs, provider.GetRequiredService<ILoggerFactory>());
    }

    private static AgentAssistFeatureState Deepgram(Replica writer, string apiKey) => new(
        Enabled: true,
        Provider: "deepgram",
        ProtectedCredentials: writer.Protector.Protect(new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = apiKey }),
        UpdatedAt: DateTimeOffset.UtcNow,
        UpdatedBy: Admin);

    private async Task<long> CountKeysAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var cmd = dataSource.CreateCommand("SELECT count(*) FROM data_protection_keys");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task DrainAsync(SpeechRecognizer recognizer)
    {
        await foreach (var _ in recognizer.StreamAsync(Frames(), AudioFormat.Slin16Mono8kHz))
        {
            // Nothing is recognized by the recording recognizer.
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Frames()
    {
        await Task.Yield();
        yield return new byte[320];
    }

    // As PostgresAgentAssistFeatureToggleLiveDbTests: every embedded Migrations/*.sql of Storage.Postgres, in order.
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

    private sealed class Disposer(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }
}
