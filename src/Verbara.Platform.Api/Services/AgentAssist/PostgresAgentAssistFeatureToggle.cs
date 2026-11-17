using System.Buffers;
using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist feature state over the Platform database: one <c>agent_assist_feature_state</c> row that every
/// replica reads, so an administrator's change made through any replica is the state of all of them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Direct reads.</b> The administrator's GET and PUT (<see cref="ReadAsync"/>, <see cref="SetAsync"/>) and a
/// session's recognizer read (<see cref="ReadForSessionAsync"/>) read and write the row directly, never through the
/// cache, so a GET on one replica right after a PUT on another answers the new state, and a session whose read starts
/// after the PUT uses its key and its off-switch.
/// </para>
/// <para>
/// <b>Engine path.</b> <see cref="GetCurrentAsync"/> is the engine's per-call check of <c>enabled</c>. It is served from
/// a cache of at most <see cref="CacheWindow"/> per replica, refreshed by every read and write this replica makes, so
/// the cache can delay only whether a snoop is created, never the key or the off-switch a session uses. On a database
/// error it answers the last state this replica knew (the disabled state when it knew none), logged and counted, and
/// tries the database again once the window has passed.
/// </para>
/// <para>
/// <b>Seeding.</b> Before its first read the replica writes the <c>AgentAssist</c> configuration section into the row:
/// an <c>INSERT … ON CONFLICT (id) DO NOTHING</c> when no row exists, otherwise an <c>UPDATE … WHERE updated_by =
/// 'appsettings'</c>, so a changed configuration applies at restart until an administrator writes the row, and never
/// after. Two replicas seeding at once leave one row, and neither overwrites an administrator's write.
/// </para>
/// <para>
/// Credentials are stored as a JSON object of values already encrypted by
/// <see cref="AgentAssistCredentialsProtector"/>; this class never decrypts them and never logs them.
/// </para>
/// </remarks>
internal sealed partial class PostgresAgentAssistFeatureToggle : IAgentAssistFeatureStore, IDisposable
{
    /// <summary>The longest the engine path answers from its cache before it reads the row again.</summary>
    internal static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(5);

    private const string ReadSql =
        "SELECT enabled, provider, protected_credentials::text AS protected_credentials, updated_at, updated_by " +
        "FROM agent_assist_feature_state WHERE id = 1";

    private const string SeedInsertSql =
        "INSERT INTO agent_assist_feature_state (id, enabled, provider, protected_credentials, updated_at, updated_by) " +
        "VALUES (1, @Enabled, @Provider, @Credentials::jsonb, @UpdatedAt, @UpdatedBy) " +
        "ON CONFLICT (id) DO NOTHING";

    private const string SeedUpdateSql =
        "UPDATE agent_assist_feature_state " +
        "SET enabled = @Enabled, provider = @Provider, protected_credentials = @Credentials::jsonb, " +
        "updated_at = @UpdatedAt " +
        "WHERE id = 1 AND updated_by = @UpdatedBy";

    private const string WriteSql =
        "INSERT INTO agent_assist_feature_state (id, enabled, provider, protected_credentials, updated_at, updated_by) " +
        "VALUES (1, @Enabled, @Provider, @Credentials::jsonb, @UpdatedAt, @UpdatedBy) " +
        "ON CONFLICT (id) DO UPDATE SET enabled = EXCLUDED.enabled, provider = EXCLUDED.provider, " +
        "protected_credentials = EXCLUDED.protected_credentials, updated_at = EXCLUDED.updated_at, " +
        "updated_by = EXCLUDED.updated_by " +
        "RETURNING enabled, provider, protected_credentials::text AS protected_credentials, updated_at, updated_by";

    private readonly NpgsqlDataSource _dataSource;
    private readonly AgentAssistOptions _seedOptions;
    private readonly AgentAssistCredentialsProtector _protector;
    private readonly AgentAssistToggleMetrics _metrics;
    private readonly ILogger<PostgresAgentAssistFeatureToggle> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    private volatile bool _seeded;

    /// <summary>The last state this replica read or wrote, and when (a <see cref="TimeProvider"/> timestamp).</summary>
    private volatile Known? _lastKnown;

    /// <summary>
    /// What the engine path answers until <see cref="CacheWindow"/> has passed since <see cref="Known.ReadAt"/>: the last
    /// state read or written, or, after a database error, the state answered then, so an outage costs one attempt per
    /// window instead of one per call.
    /// </summary>
    private volatile Known? _engineCache;

    /// <summary>Builds the toggle for one replica.</summary>
    /// <param name="dataSource">The core Platform data source (the database migration 031 created the table in).</param>
    /// <param name="options">The <c>AgentAssist</c> section, which seeds the row until an administrator writes it.</param>
    /// <param name="protector">Encrypts the configured credentials before they are written.</param>
    /// <param name="metrics">The stale-read counter.</param>
    /// <param name="logger">Logs a read answered with the last known state.</param>
    /// <param name="timeProvider">Paces the engine-path cache; the system clock when omitted.</param>
    public PostgresAgentAssistFeatureToggle(
        NpgsqlDataSource dataSource,
        IOptions<AgentAssistOptions> options,
        AgentAssistCredentialsProtector protector,
        AgentAssistToggleMetrics metrics,
        ILogger<PostgresAgentAssistFeatureToggle> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _seedOptions = options.Value;
        _protector = protector;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>The engine path: cached for at most <see cref="CacheWindow"/>, the last known state on a database error.</remarks>
    public async ValueTask<AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default)
    {
        var cached = _engineCache;
        if (cached is not null && _time.GetElapsedTime(cached.ReadAt) < CacheWindow)
            return cached.State;

        try
        {
            return await ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStoreFailure(ex) && !ct.IsCancellationRequested)
        {
            _metrics.RecordStale(AgentAssistToggleMetrics.EnginePath);
            LogStale(_logger, ex, AgentAssistToggleMetrics.EnginePath);
            var state = _lastKnown?.State ?? AgentAssistFeatureState.Disabled;
            _engineCache = new Known(state, _time.GetTimestamp());
            return state;
        }
    }

    /// <inheritdoc />
    public async ValueTask<AgentAssistFeatureState> ReadAsync(CancellationToken ct = default)
    {
        await EnsureSeededAsync(ct).ConfigureAwait(false);
        var state = await _dataSource.QueryFirstOrDefaultAsync(ReadSql, static _ => { }, Map, ct).ConfigureAwait(false);
        if (state is null)
        {
            // The row was deleted after this replica seeded it: seed again at the next read.
            _seeded = false;
            state = AgentAssistFeatureState.Disabled;
        }

        Remember(state);
        return state;
    }

    /// <inheritdoc />
    public async ValueTask<AgentAssistFeatureState> ReadForSessionAsync(CancellationToken ct = default)
    {
        try
        {
            return await ReadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsStoreFailure(ex) && !ct.IsCancellationRequested)
        {
            _metrics.RecordStale(AgentAssistToggleMetrics.RecognizerPath);
            LogStale(_logger, ex, AgentAssistToggleMetrics.RecognizerPath);
            return _lastKnown?.State ?? AgentAssistFeatureState.Disabled;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes the row directly and returns what was stored: the provider trimmed and lowercased, <c>updatedAt</c> the
    /// clock's now when the caller gave none.
    /// </remarks>
    public async ValueTask<AgentAssistFeatureState> SetAsync(AgentAssistFeatureState desired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentException.ThrowIfNullOrWhiteSpace(desired.UpdatedBy, nameof(desired));

        var row = desired with
        {
            Provider = string.IsNullOrWhiteSpace(desired.Provider) ? null : desired.Provider.Trim().ToLowerInvariant(),
            UpdatedAt = desired.UpdatedAt ?? _time.GetUtcNow(),
        };
        var stored = await _dataSource.QuerySingleAsync(WriteSql, p => Bind(p, row), Map, ct).ConfigureAwait(false);
        Remember(stored);
        return stored;
    }

    private async ValueTask EnsureSeededAsync(CancellationToken ct)
    {
        if (_seeded)
            return;

        await _seedLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_seeded)
                return;

            var seed = AgentAssistFeatureSeed.FromOptions(_seedOptions, _protector, _time.GetUtcNow());
            var inserted = await _dataSource.ExecuteAsync(SeedInsertSql, p => Bind(p, seed), ct).ConfigureAwait(false);
            if (inserted == 0)
                await _dataSource.ExecuteAsync(SeedUpdateSql, p => Bind(p, seed), ct).ConfigureAwait(false);
            _seeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _seedLock.Dispose();

    private void Remember(AgentAssistFeatureState state)
    {
        var known = new Known(state, _time.GetTimestamp());
        _lastKnown = known;
        _engineCache = known;
    }

    private static void Bind(NpgsqlParameterCollection p, AgentAssistFeatureState state)
    {
        p.Add(new NpgsqlParameter("Enabled", NpgsqlDbType.Boolean) { Value = state.Enabled });
        p.Add(new NpgsqlParameter("Provider", NpgsqlDbType.Text) { Value = (object?)state.Provider ?? DBNull.Value });
        p.Add(new NpgsqlParameter("Credentials", NpgsqlDbType.Text)
        {
            Value = state.ProtectedCredentials is { Count: > 0 } credentials ? WriteCredentials(credentials) : DBNull.Value,
        });
        p.Add(new NpgsqlParameter("UpdatedAt", NpgsqlDbType.TimestampTz) { Value = state.UpdatedAt!.Value });
        p.Add(new NpgsqlParameter("UpdatedBy", NpgsqlDbType.Text) { Value = state.UpdatedBy! });
    }

    private static AgentAssistFeatureState Map(NpgsqlDataReader reader)
    {
        var credentials = reader.GetStringOrNull("protected_credentials");
        return new AgentAssistFeatureState(
            Enabled: reader.GetBoolean("enabled"),
            Provider: reader.GetStringOrNull("provider"),
            ProtectedCredentials: credentials is null ? null : ReadCredentials(credentials),
            UpdatedAt: reader.GetDateTimeOffset("updated_at"),
            UpdatedBy: reader.GetString("updated_by"));
    }

    /// <summary>The encrypted credentials as a JSON object of strings (no reflection: written member by member).</summary>
    private static string WriteCredentials(IReadOnlyDictionary<string, string> credentials)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in credentials)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static Dictionary<string, string> ReadCredentials(string json)
    {
        using var document = JsonDocument.Parse(json);
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in document.RootElement.EnumerateObject())
            credentials[member.Name] = member.Value.GetString() ?? string.Empty;
        return credentials;
    }

    /// <summary>A database or network failure, or a timeout: the read answers the last known state instead.</summary>
    private static bool IsStoreFailure(Exception ex) =>
        ex is DbException or TimeoutException or IOException or SocketException or InvalidOperationException
            or OperationCanceledException;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent Assist could not read its feature state ({Path}); using the last state this replica knew")]
    private static partial void LogStale(ILogger logger, Exception exception, string path);

    private sealed record Known(AgentAssistFeatureState State, long ReadAt);
}
