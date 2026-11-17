using System.Collections.Concurrent;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.AgentAssist.Domain;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Sessions;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// Bridges <see cref="AgentAssistSupervisor"/> observable streams into
/// <see cref="PlatformEventBus"/> so agent assist events flow out via SSE.
/// </summary>
/// <remarks>
/// Each session's events carry the identity its <see cref="AgentAssistSessionIdentity"/> binds when they are published
/// (agent-assist-works-end-to-end, design D14): the owner tenant, the agent and the conversation are re-evaluated, never
/// read once when the session starts, and an event emitted before they are known is held and published later, in order.
/// Each event is published once, under the session's lock; a failed publish is logged, not retried.
/// </remarks>
internal sealed partial class AgentAssistBridge : IHostedService, IDisposable
{
    private readonly AgentAssistSupervisor _supervisor;
    private readonly PlatformEventBus _eventBus;
    private readonly IConversationStore _conversationStore;
    private readonly ILogger<AgentAssistBridge> _logger;
    private readonly IAgentAssistLiveSessionStore? _liveSessionStore;
    private readonly TimeProvider _time;
    private readonly AgentAssistHeldEventMetrics? _heldMetrics;

    // Per-session subscriptions, identity and Monitor record.
    private readonly ConcurrentDictionary<string, Attached> _sessions = new();

    private IDisposable? _startedSub;
    private IDisposable? _endedSub;

    public AgentAssistBridge(
        AgentAssistSupervisor supervisor,
        PlatformEventBus eventBus,
        IConversationStore conversationStore,
        ILogger<AgentAssistBridge> logger,
        IAgentAssistLiveSessionStore? liveSessionStore = null,
        TimeProvider? time = null,
        AgentAssistHeldEventMetrics? heldMetrics = null)
    {
        _supervisor = supervisor;
        _eventBus = eventBus;
        _conversationStore = conversationStore;
        _logger = logger;
        _liveSessionStore = liveSessionStore is { IsShared: true } ? liveSessionStore : null;
        _time = time ?? TimeProvider.System;
        _heldMetrics = heldMetrics;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // OnNext must be synchronous and never throw: Attach subscribes before it returns and catches everything.
        _startedSub = _supervisor.SessionStarted.Subscribe(s => Attach(AssistSessionFeed.Of(s)));
        _endedSub   = _supervisor.SessionEnded.Subscribe(s => Detach(s.SessionId));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts bridging one announced session. Runs synchronously and awaits nothing: the session's streams do not
    /// replay, so every subscription is in place before this returns and the session can emit (design D14, "The bridge
    /// subscribes before its first await"). The conversation lookup starts after the subscriptions.
    /// </summary>
    internal void Attach(AssistSessionFeed session)
    {
        try
        {
            var sessionId = session.SessionId;
            var identity = new AgentAssistSessionIdentity(
                sessionId, session.StartTenant, session.Call, _conversationStore, _eventBus, _time, _logger, _heldMetrics);

            // The Monitor record (design D12): filed under the session's owner tenant, re-evaluated on each event below,
            // at each heartbeat and at each bind check, and removed at the end.
            AgentAssistSessionFiling? filing = null;
            if (_liveSessionStore is not null)
            {
                filing = AgentAssistSessionFiling.Start(
                    sessionId, session.StartTenant, session.Call, _liveSessionStore, _time, _logger,
                    session.LatestCallerSentiment);
            }

            var suggestionSub = session.Suggestions.Subscribe(s =>
            {
                filing?.OnEmission();
                identity.Emit(
                    (tenantId, agentId, conversationId) => new AgentAssistSuggestionEvent(
                        tenantId, sessionId, agentId, conversationId,
                        s.Id, s.Text, s.Priority.ToString(), s.Source.ToString(), s.TriggerPhrase),
                    Log.PublishSuggestionFailed);
            });

            var sentimentSub = session.Sentiment.Subscribe(r =>
            {
                filing?.OnEmission();
                identity.Emit(
                    (tenantId, agentId, conversationId) => new AgentAssistSentimentEvent(
                        tenantId, sessionId, agentId, conversationId,
                        r.Speaker.ToString(), r.Score, r.Label.ToString(), r.TriggerWords),
                    Log.PublishSentimentFailed);
            });

            var complianceSub = session.ComplianceAlerts.Subscribe(a =>
            {
                filing?.OnEmission();
                identity.Emit(
                    (tenantId, agentId, conversationId) => new AgentAssistComplianceAlertEvent(
                        tenantId, sessionId, agentId, conversationId,
                        a.RuleId, a.Phrase, a.Severity.ToString()),
                    Log.PublishComplianceAlertFailed);
            });

            var transcriptSub = session.Transcripts.Subscribe(t =>
            {
                filing?.OnEmission();
                identity.Emit(
                    (tenantId, agentId, conversationId) => new AgentAssistTranscriptEvent(
                        tenantId, sessionId, agentId, conversationId,
                        t.Speaker.ToString(), t.Text, IsFinal: true),
                    Log.PublishTranscriptFailed);
            });

            _sessions[sessionId] = new Attached(
                new CompositeDisposable(suggestionSub, sentimentSub, complianceSub, transcriptSub), identity, filing);

            // After every Subscribe: the first lookup, when the owner is already known.
            identity.Refresh();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.SessionStartFailed(_logger, session.SessionId, ex);
        }
    }

    /// <summary>The session ended: stops its subscriptions, ends its identity and removes its Monitor record.</summary>
    internal void Detach(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var attached))
            return;

        attached.Subscriptions.Dispose();
        attached.Identity.End();
        if (attached.Filing is { } filing)
            _ = filing.EndAsync();
    }

    /// <summary>
    /// Derives the platform agentId for agent-assist events: the id parsed from the realtime member
    /// interface (<c>PJSIP/{tenant}-agent-{id}</c>) when the tenant is known, else the raw
    /// <paramref name="fallbackAgentId"/> (<c>CallSession.AgentId</c>, often empty for app_queue),
    /// else empty. Guarding the parse on a non-empty tenant avoids a blank-prefix match that would
    /// silently collapse the id and drop every agent-targeted event client-side (review finding #13).
    /// </summary>
    internal static string DeriveAgentId(string tenantId, string? agentInterface, string? fallbackAgentId)
    {
        var parsed = string.IsNullOrEmpty(tenantId)
            ? null
            : AgentInterfaceParser.ExtractAgentId(tenantId, agentInterface)?.Value;
        return parsed ?? fallbackAgentId ?? string.Empty;
    }

    public void Dispose()
    {
        _startedSub?.Dispose();
        _endedSub?.Dispose();

        // The records are left to expire (design D12): this replica stops, and its heartbeats with it.
        foreach (var attached in _sessions.Values)
        {
            attached.Subscriptions.Dispose();
            attached.Identity.Dispose();
            attached.Filing?.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>One announced session as the bridge reads it: its id, start-time tenant, call and four streams.</summary>
    internal sealed record AssistSessionFeed(
        string SessionId,
        string? StartTenant,
        CallSession? Call,
        IObservable<TranscriptSegment> Transcripts,
        IObservable<AgentSuggestion> Suggestions,
        IObservable<SentimentReading> Sentiment,
        IObservable<ComplianceAlert> ComplianceAlerts,
        Func<string?>? LatestCallerSentiment = null)
    {
        internal static AssistSessionFeed Of(AgentAssistSession session) => new(
            session.SessionId, session.TenantId, session.CallSession, session.Transcripts, session.Suggestions,
            session.Sentiment, session.ComplianceAlerts, () => AgentAssistLiveSessions.LatestCallerSentimentOf(session));
    }

    private sealed record Attached(IDisposable Subscriptions, AgentAssistSessionIdentity Identity, AgentAssistSessionFiling? Filing);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing suggestion for session {SessionId}")]
        public static partial void PublishSuggestionFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing sentiment for session {SessionId}")]
        public static partial void PublishSentimentFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing compliance alert for session {SessionId}")]
        public static partial void PublishComplianceAlertFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[AgentAssistBridge] Error publishing transcript for session {SessionId}")]
        public static partial void PublishTranscriptFailed(ILogger logger, string sessionId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[AgentAssistBridge] Failed to start session {SessionId}")]
        public static partial void SessionStartFailed(ILogger logger, string sessionId, Exception exception);
    }

    /// <summary>Composes multiple <see cref="IDisposable"/> instances into one.</summary>
    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _disposables;

        internal CompositeDisposable(params IDisposable[] disposables) =>
            _disposables = disposables;

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try { d.Dispose(); }
                catch { /* best-effort */ }
            }
        }
    }
}
