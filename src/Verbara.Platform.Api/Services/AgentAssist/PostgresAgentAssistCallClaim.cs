using System.Data.Common;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Verbara.Sdk.Data.Npgsql;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist per-call claim over the Platform database: one <c>agent_assist_call_claims</c> row per physical
/// call, so that at most one replica runs an Agent Assist session for a call, even while two replicas both believe
/// they hold the voice AMI lease.
/// </summary>
/// <remarks>
/// <para>
/// <b>Claim.</b> One statement, <c>INSERT … ON CONFLICT (call_key) DO UPDATE … WHERE … RETURNING</c>: a row comes back
/// only to the winner. A row held by another owner is taken over only once it is older than
/// <see cref="AbandonAge"/>, whatever that owner's liveness. The same owner replaces its own row only with a higher
/// generation, so of two setups for one call the later keeps the claim in either commit order. Under READ COMMITTED
/// a conflicting statement waits for the other's commit and then evaluates the <c>WHERE</c> against the committed
/// row, so two claimants racing for an absent key never both win.
/// </para>
/// <para>
/// <b>Release.</b> Deletes the row only while it still carries the caller's claim id, so a superseded setup's late
/// release never removes the claim that replaced it. Idempotent; never throws: a failure is logged and counted
/// (<c>release_error</c>), and the row it leaves blocks only another owner, until the abandon age.
/// </para>
/// <para>
/// <b>Failure.</b> A claim that fails throws (counted <c>error</c>), and the engine fails closed. It may have been
/// written before the error reached the caller (an ambiguous claim); the claim id is the caller's, so its release
/// deletes the row if it was written and nothing otherwise.
/// </para>
/// <para>
/// <b>Abandon age.</b> The claim is not renewed, so the one-session promise holds while the holder's claim is younger
/// than 24 h: a call still assisted after that can gain a second session if another replica handles a connect for
/// it. Rows older than that are deleted at most once an hour, after a claim.
/// </para>
/// </remarks>
internal sealed partial class PostgresAgentAssistCallClaim : IAgentAssistCallClaim
{
    /// <summary>A claim older than this may be taken over by another owner, and is swept.</summary>
    internal static readonly TimeSpan AbandonAge = TimeSpan.FromHours(24);

    /// <summary>The store sweeps abandoned rows at most this often.</summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private const string ClaimSql =
        "INSERT INTO agent_assist_call_claims AS c (call_key, claim_id, owner, generation, claimed_at) " +
        "VALUES (@Key, @ClaimId, @Owner, @Generation, statement_timestamp()) " +
        "ON CONFLICT (call_key) DO UPDATE " +
        "SET claim_id = EXCLUDED.claim_id, owner = EXCLUDED.owner, generation = EXCLUDED.generation, " +
        "claimed_at = EXCLUDED.claimed_at " +
        "WHERE (c.owner = EXCLUDED.owner AND c.generation < EXCLUDED.generation) " +
        "OR c.claimed_at < statement_timestamp() - interval '24 hours' " +
        "RETURNING c.claim_id";

    private const string ReleaseSql =
        "DELETE FROM agent_assist_call_claims WHERE call_key = @Key AND claim_id = @ClaimId";

    private const string SweepSql =
        "DELETE FROM agent_assist_call_claims WHERE claimed_at < statement_timestamp() - interval '24 hours'";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _owner;
    private readonly AgentAssistCallClaimMetrics _metrics;
    private readonly ILogger<PostgresAgentAssistCallClaim> _logger;
    private readonly TimeProvider _time;

    /// <summary>The earliest instant (timestamp ticks of <see cref="_time"/>) the next sweep may run; 0: at the first claim.</summary>
    private long _nextSweepTicks;

    /// <summary>Builds the claim store for one replica.</summary>
    /// <param name="dataSource">The core Platform data source (the database migration 031 created the table in).</param>
    /// <param name="owner">This replica's claimant identity: its <c>Cluster:Leadership</c> <c>InstanceId</c>.</param>
    /// <param name="metrics">The claim counter.</param>
    /// <param name="logger">Logs a failed release and a failed sweep.</param>
    /// <param name="timeProvider">Paces the sweep; the system clock when omitted.</param>
    public PostgresAgentAssistCallClaim(
        NpgsqlDataSource dataSource,
        string owner,
        AgentAssistCallClaimMetrics metrics,
        ILogger<PostgresAgentAssistCallClaim> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _dataSource = dataSource;
        _owner = owner;
        _metrics = metrics;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The row key of <paramref name="call"/>: <c>ServerId + "/" + LinkedId</c>.</summary>
    internal static string CallKey(AgentAssistCallKey call) => call.ServerId + "/" + call.LinkedId;

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The call has a blank <c>LinkedId</c>; no statement is sent.</exception>
    public async ValueTask<bool> TryClaimAsync(
        AgentAssistCallKey call, Guid claimId, long generation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(call.LinkedId))
        {
            _metrics.Record(AgentAssistCallClaimMetrics.Error);
            throw new ArgumentException("An Agent Assist call claim needs the call's LinkedId.", nameof(call));
        }

        var key = CallKey(call);
        Guid? won;
        try
        {
            won = await _dataSource.ExecuteScalarAsync<Guid?>(ClaimSql, p =>
            {
                p.Add(new NpgsqlParameter("Key", NpgsqlDbType.Text) { Value = key });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Uuid) { Value = claimId });
                p.Add(new NpgsqlParameter("Owner", NpgsqlDbType.Text) { Value = _owner });
                p.Add(new NpgsqlParameter("Generation", NpgsqlDbType.Bigint) { Value = generation });
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The statement may have committed before the error reached us; the caller releases by claimId.
            _metrics.Record(AgentAssistCallClaimMetrics.Error);
            throw;
        }

        var claimed = won == claimId;
        _metrics.Record(claimed ? AgentAssistCallClaimMetrics.Claimed : AgentAssistCallClaimMetrics.Held);
        if (!claimed)
            LogHeld(_logger, key);

        await SweepIfDueAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    /// <inheritdoc />
    public async ValueTask ReleaseAsync(AgentAssistCallKey call, Guid claimId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(call.LinkedId))
            return; // No claim can have been written for it.

        var key = CallKey(call);
        try
        {
            await _dataSource.ExecuteAsync(ReleaseSql, p =>
            {
                p.Add(new NpgsqlParameter("Key", NpgsqlDbType.Text) { Value = key });
                p.Add(new NpgsqlParameter("ClaimId", NpgsqlDbType.Uuid) { Value = claimId });
            }, cancellationToken).ConfigureAwait(false);
            _metrics.Record(AgentAssistCallClaimMetrics.Released);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            _metrics.Record(AgentAssistCallClaimMetrics.ReleaseError);
            LogReleaseFailed(_logger, ex, key);
        }
    }

    private async ValueTask SweepIfDueAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcTicks;
        var due = Interlocked.Read(ref _nextSweepTicks);
        if (now < due || Interlocked.CompareExchange(ref _nextSweepTicks, now + SweepInterval.Ticks, due) != due)
            return;

        try
        {
            var deleted = await _dataSource.ExecuteAsync(SweepSql, static _ => { }, cancellationToken).ConfigureAwait(false);
            if (deleted > 0)
                LogSwept(_logger, deleted);
        }
        catch (Exception ex) when (IsStoreFailure(ex))
        {
            LogSweepFailed(_logger, ex);
        }
    }

    /// <summary>
    /// A failure of the store or of the call's token: a database or network error, a timeout or a cancellation. A
    /// failed release never stops the session's teardown, and a failed sweep (housekeeping after a settled claim)
    /// never fails the claim; both are logged instead.
    /// </summary>
    private static bool IsStoreFailure(Exception ex) =>
        ex is DbException or OperationCanceledException or TimeoutException or IOException or SocketException
            or InvalidOperationException;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Agent Assist call {CallKey} is held by another claim")]
    private static partial void LogHeld(ILogger logger, string callKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent Assist could not release its claim on call {CallKey}; another replica cannot assist that call until the claim is 24 hours old")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string callKey);

    [LoggerMessage(Level = LogLevel.Information, Message = "Agent Assist swept {Count} call claims older than 24 hours")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Agent Assist could not sweep abandoned call claims; the next sweep is due in one hour")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
