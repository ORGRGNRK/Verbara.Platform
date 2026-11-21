using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The counter of Agent Assist events a session held and never published, for the operator:
/// <c>platform.agentassist.events_held_dropped</c>, tagged <c>reason</c> (Prometheus
/// <c>platform_agentassist_events_held_dropped_total{reason}</c>). No session or tenant tag: either would make the series
/// unbounded.
/// </summary>
/// <remarks>
/// Reasons: <c>tenant_unresolved</c> (the session ended before its call's tenant was known), <c>conflict</c> (the call's
/// tenant and the session's start-time tenant differ, so the session belongs to nobody) and <c>overflow</c> (more than
/// 128 events were held; the oldest goes). Built through an optional
/// <see cref="IMeterFactory"/>; a meter it creates itself is disposed with it.
/// </remarks>
internal sealed class AgentAssistHeldEventMetrics : IDisposable
{
    /// <summary>The counter's instrument name; the meter is <see cref="AgentAssistCallClaimMetrics.MeterName"/>.</summary>
    public const string HeldDroppedInstrument = "platform.agentassist.events_held_dropped";

    public const string TenantUnresolved = "tenant_unresolved";
    public const string Conflict = "conflict";
    public const string Overflow = "overflow";

    private readonly bool _ownsMeter;
    private readonly Counter<long> _dropped;

    public AgentAssistHeldEventMetrics(IMeterFactory? meterFactory = null)
    {
        _ownsMeter = meterFactory is null;
        Meter = meterFactory is null
            ? new Meter(AgentAssistCallClaimMetrics.MeterName)
            : meterFactory.Create(AgentAssistCallClaimMetrics.MeterName);
        _dropped = Meter.CreateCounter<long>(
            HeldDroppedInstrument,
            description: "Agent Assist events held for a session and dropped unpublished, by reason: tenant_unresolved, conflict, overflow.");
    }

    /// <summary>The meter the counter belongs to (tests listen to this instance only).</summary>
    internal Meter Meter { get; }

    /// <summary>Counts <paramref name="count"/> events dropped for <paramref name="reason"/>.</summary>
    public void RecordDropped(string reason, long count = 1)
    {
        if (count > 0)
            _dropped.Add(count, new KeyValuePair<string, object?>("reason", reason));
    }

    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }
}
