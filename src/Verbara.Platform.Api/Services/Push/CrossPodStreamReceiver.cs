using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;
using Verbara.Sdk.Push.Bus;
using Verbara.Sdk.Push.Events;

namespace Verbara.Platform.Api.Services.Push;

/// <summary>
/// Receives the <c>agentassist.*</c> events another replica published (agent-assist-works-end-to-end, design D12): it
/// reads the backplane envelopes the relay puts on this replica's <see cref="IPushEventBus"/>, decodes each with
/// <see cref="PlatformPushJsonContext"/> and emits the typed record on <see cref="CrossPodStreamFeed"/>, which only the
/// live stream reads. It never publishes on <see cref="PlatformEventBus"/> or <see cref="IPushEventBus"/>, so a received
/// event is not sent back onto the backplane and is not dispatched to webhooks here.
/// </summary>
/// <remarks>
/// Each envelope is handled on the bus's dispatch loop, one at a time, so one session's events reach the feed in the
/// order this replica received them. It drops, and counts, an envelope with an empty payload (the copy an older Realtime
/// image echoes back), one whose envelope tenant differs from its payload's (fails closed) and one that does not decode.
/// While no stream is open on this replica it decodes nothing.
/// </remarks>
internal sealed partial class CrossPodStreamReceiver : IHostedService, IDisposable
{
    private static readonly FrozenDictionary<string, JsonTypeInfo> Types = new Dictionary<string, JsonTypeInfo>
    {
        ["agentassist.suggestion"] = PlatformPushJsonContext.Default.AgentAssistSuggestionEvent,
        ["agentassist.sentiment"] = PlatformPushJsonContext.Default.AgentAssistSentimentEvent,
        ["agentassist.compliance_alert"] = PlatformPushJsonContext.Default.AgentAssistComplianceAlertEvent,
        ["agentassist.transcript"] = PlatformPushJsonContext.Default.AgentAssistTranscriptEvent,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly IPushEventBus _bus;
    private readonly CrossPodStreamFeed _feed;
    private readonly CrossPodStreamMetrics _metrics;
    private readonly ILogger<CrossPodStreamReceiver> _logger;
    private IDisposable? _subscription;

    public CrossPodStreamReceiver(
        IPushEventBus bus, CrossPodStreamFeed feed, CrossPodStreamMetrics metrics, ILogger<CrossPodStreamReceiver> logger)
    {
        _bus = bus;
        _feed = feed;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>The <c>OriginalEventType</c> names this receiver takes.</summary>
    internal static IReadOnlyCollection<string> EventTypes => Types.Keys;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _bus.OfType<RemotePushEvent>().Subscribe(envelope => TryAccept(envelope));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles one envelope: <see langword="true"/> when its event was handed to the live streams, else
    /// <see langword="false"/>. Never throws (it runs on the bus's dispatch loop).
    /// </summary>
    internal bool TryAccept(RemotePushEvent envelope)
    {
        if (!Types.TryGetValue(envelope.OriginalEventType, out var typeInfo) || !_feed.HasSubscribers)
            return false;

        if (envelope.RawPayload.Length == 0)
        {
            _metrics.Record(CrossPodStreamMetrics.EmptyPayload);
            Log.EmptyPayload(_logger, envelope.OriginalEventType, envelope.SourceNodeId);
            return false;
        }

        PlatformEvent? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize(envelope.RawPayload, typeInfo) as PlatformEvent;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            decoded = null;
            Log.DecodeFailed(_logger, envelope.OriginalEventType, ex);
        }

        if (decoded is null || !string.Equals(decoded.Type, envelope.OriginalEventType, StringComparison.Ordinal))
        {
            _metrics.Record(CrossPodStreamMetrics.DecodeError);
            return false;
        }

        if (!string.Equals(envelope.Metadata?.TenantId, decoded.TenantId, StringComparison.Ordinal))
        {
            _metrics.Record(CrossPodStreamMetrics.TenantMismatch);
            Log.TenantMismatch(_logger, envelope.OriginalEventType, envelope.Metadata?.TenantId, decoded.TenantId);
            return false;
        }

        try
        {
            _feed.Emit(decoded);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // A stream that ends while the event is handed to it; the other streams have it.
            Log.EmitFailed(_logger, envelope.OriginalEventType, ex);
        }

        _metrics.Record(CrossPodStreamMetrics.Delivered);
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug,
            Message = "[CrossPodStream] Dropped {EventType} from {OriginNodeId}: the envelope has no payload")]
        public static partial void EmptyPayload(ILogger logger, string eventType, string? originNodeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[CrossPodStream] Dropped {EventType}: the payload does not decode")]
        public static partial void DecodeFailed(ILogger logger, string eventType, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "[CrossPodStream] Dropped {EventType}: the envelope's tenant {EnvelopeTenant} is not the payload's {PayloadTenant}")]
        public static partial void TenantMismatch(ILogger logger, string eventType, string? envelopeTenant, string payloadTenant);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[CrossPodStream] A live stream failed on a received {EventType}")]
        public static partial void EmitFailed(ILogger logger, string eventType, Exception exception);
    }
}

/// <summary>
/// The receiver's counter: <c>platform.agentassist.crosspod_received</c>, tagged <c>outcome</c> (Prometheus
/// <c>platform_agentassist_crosspod_received_total{outcome}</c>): <c>delivered</c>, <c>empty_payload</c>,
/// <c>tenant_mismatch</c>, <c>decode_error</c>. No tenant or session tag. Owns its meter.
/// </summary>
internal sealed class CrossPodStreamMetrics : IDisposable
{
    /// <summary>The counter's instrument name.</summary>
    public const string ReceivedInstrument = "platform.agentassist.crosspod_received";

    public const string Delivered = "delivered";
    public const string EmptyPayload = "empty_payload";
    public const string TenantMismatch = "tenant_mismatch";
    public const string DecodeError = "decode_error";

    private readonly Counter<long> _received;

    public CrossPodStreamMetrics(IMeterFactory? meterFactory = null)
    {
        Meter = meterFactory is null
            ? new Meter(AgentAssistCallClaimMetrics.MeterName)
            : meterFactory.Create(AgentAssistCallClaimMetrics.MeterName);
        _received = Meter.CreateCounter<long>(
            ReceivedInstrument,
            description: "Agent Assist events received from another replica, by outcome: delivered, empty_payload, tenant_mismatch, decode_error.");
    }

    /// <summary>The meter the counter belongs to (tests listen to this instance only).</summary>
    internal Meter Meter { get; }

    /// <summary>Counts one envelope of <paramref name="outcome"/>.</summary>
    public void Record(string outcome) =>
        _received.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    /// <inheritdoc />
    public void Dispose() => Meter.Dispose();
}
