using StackExchange.Redis;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// Keeps one live session's Monitor record (agent-assist-works-end-to-end, design D12) on the replica that runs it. The
/// record is filed under the session's owner tenant, <see cref="AgentAssistLiveSessions.OwnerTenantOf"/> the call's tenant
/// (read live) and the session's start-time tenant, re-evaluated at every bind check and every heartbeat, never fixed
/// when the session starts:
/// <list type="bullet">
/// <item>no owner yet: no record, and a bind check every <see cref="BindCheckInterval"/> until there is one;</item>
/// <item>an owner: the record is filed under it and refreshed every <see cref="HeartbeatInterval"/>, living
/// <see cref="RecordTtl"/> past its last filing;</item>
/// <item>the owner changed (a session with no start-time tenant whose call is stamped with another tenant): the record
/// is removed from the former owner and filed under the new one. It holds no conversation id, so the new owner's row
/// never shows the former owner's conversation;</item>
/// <item>the two tenants conflict: the record is removed, so the session is listed to nobody;</item>
/// <item>the session ended: the record is removed and nothing re-files it.</item>
/// </list>
/// The record also carries the label of the session's latest caller sentiment reading (design D13): an event after which
/// that label differs from the one last filed re-files the record at once.
/// Every write runs on one queue per session, so a heartbeat that runs after the end re-writes nothing.
/// </summary>
internal sealed partial class AgentAssistSessionFiling : IDisposable
{
    /// <summary>How often a filed record is refreshed, and re-evaluated with no event (the bound of a silent conflict).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long a record lives past its last filing: three heartbeats.</summary>
    public static readonly TimeSpan RecordTtl = TimeSpan.FromSeconds(15);

    /// <summary>How often an unfiled session is re-evaluated.</summary>
    public static readonly TimeSpan BindCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly string _sessionId;
    private readonly string? _sessionTenant;
    private readonly CallSession? _call;
    private readonly Func<string?> _sentiment;
    private readonly IAgentAssistLiveSessionStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly ITimer _heartbeat;
    private readonly ITimer _bindCheck;
    private Task _tail = Task.CompletedTask;
    private bool _ended;

    // Written only by the queued work, which runs one item at a time.
    private string? _filedUnder;

    // Read on each event without the queue: the owner the record was last filed under, or null.
    private volatile string? _lastOwner;

    // Read on each event without the queue: the sentiment label the record was last filed with, or null.
    private volatile string? _lastSentiment;

    private AgentAssistSessionFiling(
        string sessionId, string? sessionTenant, CallSession? call, IAgentAssistLiveSessionStore store, TimeProvider time,
        ILogger logger, Func<string?>? sentiment)
    {
        _sessionId = sessionId;
        _sessionTenant = sessionTenant;
        _call = call;
        _sentiment = sentiment ?? NoSentiment;
        _store = store;
        _time = time;
        _logger = logger;
        _heartbeat = time.CreateTimer(_ => Enqueue(), null, HeartbeatInterval, HeartbeatInterval);
        _bindCheck = time.CreateTimer(_ => Enqueue(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Starts keeping the record of session <paramref name="sessionId"/>, whose start-time tenant is
    /// <paramref name="sessionTenant"/>, on <paramref name="call"/>: evaluates it now, then on every heartbeat.
    /// <paramref name="sentiment"/> reads the label of the session's latest caller sentiment reading (none when omitted).
    /// </summary>
    public static AgentAssistSessionFiling Start(
        string sessionId, string? sessionTenant, CallSession? call, IAgentAssistLiveSessionStore store, TimeProvider time,
        ILogger logger, Func<string?>? sentiment = null)
    {
        var filing = new AgentAssistSessionFiling(sessionId, sessionTenant, call, store, time, logger, sentiment);
        filing.Enqueue();
        return filing;
    }

    /// <summary>The tenant the record is filed under now, or <see langword="null"/>.</summary>
    internal string? FiledUnder => _lastOwner;

    /// <summary>The queued work, done: tests await it after advancing the clock.</summary>
    internal Task WhenIdleAsync()
    {
        lock (_gate)
            return _tail;
    }

    /// <summary>
    /// A bind check on an event of the session: queues a re-evaluation when the owner is no longer the one the record is
    /// filed under, or a filed record's sentiment label changed. Two reads otherwise.
    /// </summary>
    public void OnEmission()
    {
        var owner = OwnerNow();
        if (!string.Equals(owner, _lastOwner, StringComparison.Ordinal)
            || (owner is not null && !string.Equals(_sentiment(), _lastSentiment, StringComparison.Ordinal)))
            Enqueue();
    }

    /// <summary>The session ended: removes the record and stops every timer. Later ticks write nothing.</summary>
    public Task EndAsync()
    {
        lock (_gate)
        {
            if (_ended)
                return _tail;
            _ended = true;
            _heartbeat.Dispose();
            _bindCheck.Dispose();
            _tail = Then(_tail, RemoveFiledAsync);
            return _tail;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _heartbeat.Dispose();
        _bindCheck.Dispose();
    }

    private string? OwnerNow() => AgentAssistLiveSessions.OwnerTenantOf(_call?.TenantId, _sessionTenant);

    private static string? NoSentiment() => null;

    private void Enqueue()
    {
        lock (_gate)
        {
            if (_ended)
                return;
            _tail = Then(_tail, ReevaluateAsync);
        }
    }

    private async Task Then(Task previous, Func<Task> work)
    {
        // A failed item never stops the queue: the next heartbeat writes again.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or OperationCanceledException or ObjectDisposedException)
        {
            Log.WriteFailed(_logger, _sessionId, ex);
        }
    }

    private async Task ReevaluateAsync()
    {
        lock (_gate)
        {
            if (_ended)
                return;
        }

        var owner = OwnerNow();
        if (_filedUnder is { } filed && !string.Equals(filed, owner, StringComparison.Ordinal))
        {
            // Conflict, or a new owner: the former owner stops listing it now.
            await _store.RemoveAsync(filed, _sessionId, CancellationToken.None).ConfigureAwait(false);
            _filedUnder = null;
            _lastOwner = null;
            Log.Unfiled(_logger, _sessionId, filed, owner);
        }

        if (owner is null)
        {
            _bindCheck.Change(BindCheckInterval, BindCheckInterval);
            return;
        }

        var sentiment = _sentiment();
        var record = LiveSessionRecord.Of(_sessionId, owner, _call, sentiment);
        await _store.FileAsync(record, _time.GetUtcNow() + RecordTtl, CancellationToken.None).ConfigureAwait(false);
        _filedUnder = owner;
        _lastOwner = owner;
        _lastSentiment = sentiment;
        _bindCheck.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private async Task RemoveFiledAsync()
    {
        if (_filedUnder is not { } filed)
            return;
        await _store.RemoveAsync(filed, _sessionId, CancellationToken.None).ConfigureAwait(false);
        _filedUnder = null;
        _lastOwner = null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[AgentAssistBridge] Could not update the Monitor record of session {SessionId}")]
        public static partial void WriteFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "[AgentAssistBridge] Session {SessionId} is no longer listed to tenant {FormerOwner} (owner now {Owner})")]
        public static partial void Unfiled(ILogger logger, string sessionId, string formerOwner, string? owner);
    }
}
