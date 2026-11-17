using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist recognizer's counter, for the operator: <c>platform.agentassist.recognizer_unavailable</c>, tagged
/// <c>reason</c> (Prometheus <c>platform_agentassist_recognizer_unavailable_total{reason}</c>). It counts each session
/// that transcribes nothing, once per session however many streams it has.
/// </summary>
/// <remarks>
/// Reasons: <c>disabled</c> (the stored state is off), <c>provider_not_live</c> (the stored provider cannot transcribe
/// a live call, or none is stored) and <c>missing_value</c> (a value the provider needs is missing or unusable). Built
/// through an optional <see cref="IMeterFactory"/>; a meter it creates itself is disposed with it.
/// </remarks>
internal sealed class AgentAssistRecognizerMetrics : IDisposable
{
    /// <summary>The counter's instrument name; the meter is <see cref="AgentAssistCallClaimMetrics.MeterName"/>.</summary>
    public const string RecognizerUnavailableInstrument = "platform.agentassist.recognizer_unavailable";

    public const string Disabled = "disabled";
    public const string ProviderNotLive = "provider_not_live";
    public const string MissingValue = "missing_value";

    private readonly bool _ownsMeter;
    private readonly Counter<long> _unavailable;

    public AgentAssistRecognizerMetrics(IMeterFactory? meterFactory = null)
    {
        _ownsMeter = meterFactory is null;
        Meter = meterFactory is null
            ? new Meter(AgentAssistCallClaimMetrics.MeterName)
            : meterFactory.Create(AgentAssistCallClaimMetrics.MeterName);
        _unavailable = Meter.CreateCounter<long>(
            RecognizerUnavailableInstrument,
            description: "Agent Assist sessions that transcribe nothing, by reason: disabled, provider_not_live, missing_value.");
    }

    /// <summary>The meter the counter belongs to (tests listen to this instance only).</summary>
    internal Meter Meter { get; }

    /// <summary>Counts one session that transcribes nothing, for <paramref name="reason"/>.</summary>
    public void RecordUnavailable(string reason) =>
        _unavailable.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }
}
