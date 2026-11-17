using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The identity every event of one live Agent Assist session is published with (agent-assist-works-end-to-end, design
/// D14), bound late and re-evaluated, never frozen when the session starts:
/// <list type="bullet">
/// <item><c>owner</c>: <see cref="AgentAssistLiveSessions.OwnerTenantOf"/> the call's tenant (read live) and the
/// session's start-time tenant, the function the Monitor list uses;</item>
/// <item><c>agentId</c>: <see cref="AgentAssistBridge.DeriveAgentId"/> in the owner, re-derived whenever the owner
/// changes;</item>
/// <item><c>conversationId</c>: the conversation of the call's linked id in the owner, looked up at most once per
/// <see cref="LookupInterval"/> while unresolved. A change of owner drops the resolved conversation and discards a
/// lookup started under the former owner, so an event never pairs one tenant with another tenant's conversation.</item>
/// </list>
/// An event emitted while the owner is unknown is held, at most <see cref="HoldCapacity"/> (the oldest goes, counted);
/// while the owner is known and the conversation unresolved, for at most <see cref="ConversationHold"/> from the moment the
/// owner became known, then it is published with an empty conversation. Held events are published first, in emission
/// order, each with its emission time. Every publish of the session runs under one lock across the whole
/// <see cref="PlatformEventBus.Publish"/>, once: a failed publish is logged and never retried. A session whose two
/// tenants conflict publishes nothing; a session that ends with no owner drops what it held (both counted).
/// </summary>
internal sealed partial class AgentAssistSessionIdentity : IDisposable
{
    /// <summary>The most events held for one session: half of each 256-event buffer a flush passes through.</summary>
    public const int HoldCapacity = 128;

    /// <summary>How long an event waits for its conversation once the owner is known.</summary>
    public static readonly TimeSpan ConversationHold = TimeSpan.FromSeconds(3);

    /// <summary>How often the identity is re-evaluated while anything is held.</summary>
    public static readonly TimeSpan BindCheckInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>The shortest time between two conversation lookups.</summary>
    public static readonly TimeSpan LookupInterval = TimeSpan.FromSeconds(1);

    /// <summary>Builds one event from the bound tenant, agent and conversation.</summary>
    internal delegate PlatformEvent EventFactory(string tenantId, string agentId, string conversationId);

    private readonly string _sessionId;
    private readonly string? _startTenant;
    private readonly CallSession? _call;
    private readonly IConversationStore _conversations;
    private readonly PlatformEventBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly AgentAssistHeldEventMetrics? _metrics;
    private readonly Lock _gate = new();
    private readonly Queue<Pending> _held = new();

    private ITimer? _bindCheck;
    private bool _bindCheckRunning;
    private bool _ended;
    private bool _inConflict;

    private string? _owner;
    private string _agentId = string.Empty;
    private DateTimeOffset _ownerSince;

    // Bumped on every change of owner: a lookup started under an older generation is discarded when it returns.
    private long _generation;
    private string? _conversationId;
    private long _lookupGeneration = -1;
    private DateTimeOffset _lastLookupAt = DateTimeOffset.MinValue;

    public AgentAssistSessionIdentity(
        string sessionId, string? startTenant, CallSession? call, IConversationStore conversations, PlatformEventBus bus,
        TimeProvider time, ILogger logger, AgentAssistHeldEventMetrics? metrics)
    {
        _sessionId = sessionId;
        _startTenant = startTenant;
        _call = call;
        _conversations = conversations;
        _bus = bus;
        _time = time;
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>Re-evaluates the identity and starts the conversation lookup when it is due. Runs no publish.</summary>
    public void Refresh() => StartLookupIfDue();

    /// <summary>
    /// One event of the session, emitted now: published with the bound identity, or held until it is bound.
    /// <paramref name="onError"/> logs a failed publish (the event is not retried).
    /// </summary>
    public void Emit(EventFactory build, Action<ILogger, string, Exception> onError)
    {
        StartLookupIfDue();
        lock (_gate)
        {
            if (_ended)
                return;

            var now = _time.GetUtcNow();
            var item = new Pending(build, now, onError);
            ReevaluateLocked(now);
            if (_inConflict)
            {
                _metrics?.RecordDropped(AgentAssistHeldEventMetrics.Conflict);
                return;
            }

            if (IsBoundLocked(now))
            {
                FlushLocked();
                PublishLocked(item);
                return;
            }

            HoldLocked(item);
        }
    }

    /// <summary>
    /// The session ended. With an owner, what is held is published (with the conversation if it resolved); with none,
    /// it is dropped and counted. Stops the bind check; later emissions are ignored.
    /// </summary>
    public void End()
    {
        lock (_gate)
        {
            if (_ended)
                return;

            var now = _time.GetUtcNow();
            ReevaluateLocked(now);
            if (_owner is not null)
            {
                FlushLocked();
            }
            else if (_held.Count > 0)
            {
                _metrics?.RecordDropped(AgentAssistHeldEventMetrics.TenantUnresolved, _held.Count);
                Log.DroppedUnresolved(_logger, _sessionId, _held.Count);
                _held.Clear();
            }

            _ended = true;
            _bindCheck?.Dispose();
            _bindCheck = null;
            _bindCheckRunning = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _ended = true;
            _held.Clear();
            _bindCheck?.Dispose();
            _bindCheck = null;
            _bindCheckRunning = false;
        }
    }

    private void OnBindCheck()
    {
        StartLookupIfDue();
        lock (_gate)
        {
            if (_ended)
                return;

            var now = _time.GetUtcNow();
            ReevaluateLocked(now);
            if (_held.Count > 0 && IsBoundLocked(now))
                FlushLocked();
            if (_held.Count == 0)
                StopBindCheckLocked();
        }
    }

    private void StartLookupIfDue()
    {
        long generation;
        string owner;
        string linkedId;
        lock (_gate)
        {
            if (_ended)
                return;

            var now = _time.GetUtcNow();
            ReevaluateLocked(now);
            if (_owner is null || _conversationId is not null || _lookupGeneration == _generation
                || string.IsNullOrEmpty(_call?.LinkedId) || now - _lastLookupAt < LookupInterval)
                return;

            generation = _lookupGeneration = _generation;
            _lastLookupAt = now;
            owner = _owner;
            linkedId = _call.LinkedId;
        }

        _ = LookupAsync(generation, owner, linkedId);
    }

    private async Task LookupAsync(long generation, string owner, string linkedId)
    {
        string? conversationId = null;
        try
        {
            var conversation = await _conversations
                .FindByVoiceLinkedIdAsync(new TenantId(owner), linkedId, CancellationToken.None)
                .ConfigureAwait(false);
            conversationId = conversation?.ConversationId.Value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.LookupFailed(_logger, _sessionId, ex);
        }

        lock (_gate)
        {
            if (_lookupGeneration == generation)
                _lookupGeneration = -1;
            if (_ended)
                return;
            if (generation != _generation)
            {
                // Started under a former owner: its answer belongs to that tenant, never to this one.
                Log.StaleLookupDiscarded(_logger, _sessionId, owner);
                return;
            }

            if (!string.IsNullOrEmpty(conversationId))
                _conversationId = conversationId;

            var now = _time.GetUtcNow();
            if (_held.Count > 0 && IsBoundLocked(now))
            {
                FlushLocked();
                StopBindCheckLocked();
            }
        }
    }

    private void ReevaluateLocked(DateTimeOffset now)
    {
        var callTenant = string.IsNullOrWhiteSpace(_call?.TenantId) ? null : _call.TenantId;
        var startTenant = string.IsNullOrWhiteSpace(_startTenant) ? null : _startTenant;
        if (callTenant is not null && startTenant is not null && !string.Equals(callTenant, startTenant, StringComparison.Ordinal))
        {
            if (_inConflict)
                return;

            _inConflict = true;
            Log.Conflict(_logger, _sessionId, startTenant, callTenant);
            if (_held.Count > 0)
            {
                _metrics?.RecordDropped(AgentAssistHeldEventMetrics.Conflict, _held.Count);
                _held.Clear();
            }

            SetOwnerLocked(null, now);
            StopBindCheckLocked();
            return;
        }

        _inConflict = false;
        var owner = AgentAssistLiveSessions.OwnerTenantOf(callTenant, startTenant);
        if (string.Equals(owner, _owner, StringComparison.Ordinal))
            return;

        if (_owner is not null && owner is not null)
            Log.OwnerChanged(_logger, _sessionId, _owner, owner);
        SetOwnerLocked(owner, now);
    }

    private void SetOwnerLocked(string? owner, DateTimeOffset now)
    {
        if (string.Equals(owner, _owner, StringComparison.Ordinal))
            return;

        _owner = owner;
        _ownerSince = now;
        _generation++;
        _conversationId = null;
        _lastLookupAt = DateTimeOffset.MinValue;
        _agentId = owner is null ? string.Empty : AgentAssistBridge.DeriveAgentId(owner, _call?.AgentInterface, _call?.AgentId);
    }

    private bool IsBoundLocked(DateTimeOffset now) =>
        _owner is not null && !_inConflict && (_conversationId is not null || now - _ownerSince >= ConversationHold);

    private void HoldLocked(Pending item)
    {
        if (_held.Count >= HoldCapacity)
        {
            _held.Dequeue();
            _metrics?.RecordDropped(AgentAssistHeldEventMetrics.Overflow);
        }

        _held.Enqueue(item);
        Log.Held(_logger, _sessionId, _owner is null ? "owner unknown" : "conversation unresolved", _held.Count);
        if (_bindCheckRunning)
            return;

        _bindCheck ??= _time.CreateTimer(static s => ((AgentAssistSessionIdentity)s!).OnBindCheck(), this,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _bindCheck.Change(BindCheckInterval, BindCheckInterval);
        _bindCheckRunning = true;
    }

    private void StopBindCheckLocked()
    {
        if (!_bindCheckRunning)
            return;
        _bindCheck?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _bindCheckRunning = false;
    }

    private void FlushLocked()
    {
        while (_held.TryDequeue(out var item))
            PublishLocked(item);
    }

    private void PublishLocked(Pending item)
    {
        try
        {
            var evt = item.Build(_owner!, _agentId, _conversationId ?? string.Empty) with { Timestamp = item.EmittedAt };
            _bus.Publish(evt);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            item.OnError(_logger, _sessionId, ex);
        }
    }

    private readonly record struct Pending(EventFactory Build, DateTimeOffset EmittedAt, Action<ILogger, string, Exception> OnError);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug,
            Message = "[AgentAssistBridge] Holding an event of session {SessionId} ({Reason}); {Count} held")]
        public static partial void Held(ILogger logger, string sessionId, string reason, int count);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "[AgentAssistBridge] Session {SessionId} ended before its tenant was known; {Count} held events dropped")]
        public static partial void DroppedUnresolved(ILogger logger, string sessionId, int count);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[AgentAssistBridge] Session {SessionId} started under tenant {StartTenant} but its call carries tenant {CallTenant}; its events are not published")]
        public static partial void Conflict(ILogger logger, string sessionId, string startTenant, string callTenant);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "[AgentAssistBridge] Session {SessionId} moved from tenant {FormerOwner} to {Owner}; its conversation is looked up again")]
        public static partial void OwnerChanged(ILogger logger, string sessionId, string formerOwner, string owner);

        [LoggerMessage(Level = LogLevel.Debug,
            Message = "[AgentAssistBridge] Discarded the conversation lookup of session {SessionId} made under former tenant {FormerOwner}")]
        public static partial void StaleLookupDiscarded(ILogger logger, string sessionId, string formerOwner);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[AgentAssistBridge] Conversation lookup failed for session {SessionId}")]
        public static partial void LookupFailed(ILogger logger, string sessionId, Exception exception);
    }
}
