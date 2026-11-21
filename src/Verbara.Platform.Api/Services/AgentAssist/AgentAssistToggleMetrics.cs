using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist feature toggle's counter, for the operator: <c>platform.agentassist.toggle_stale</c>, tagged
/// <c>path</c> (Prometheus <c>platform_agentassist_toggle_stale_total{path}</c>). It counts each read that could not
/// reach the database and answered with the last state the replica knew instead.
/// </summary>
/// <remarks>
/// Paths: <c>engine</c> (the engine's per-call check) and <c>recognizer</c> (a session's read of the provider and
/// credentials). Built through an optional <see cref="IMeterFactory"/>; a meter it creates itself is disposed with it.
/// </remarks>
internal sealed class AgentAssistToggleMetrics : IDisposable
{
    /// <summary>The counter's instrument name; the meter is <see cref="AgentAssistCallClaimMetrics.MeterName"/>.</summary>
    public const string ToggleStaleInstrument = "platform.agentassist.toggle_stale";

    public const string EnginePath = "engine";
    public const string RecognizerPath = "recognizer";

    private readonly bool _ownsMeter;
    private readonly Counter<long> _stale;

    public AgentAssistToggleMetrics(IMeterFactory? meterFactory = null)
    {
        _ownsMeter = meterFactory is null;
        Meter = meterFactory is null
            ? new Meter(AgentAssistCallClaimMetrics.MeterName)
            : meterFactory.Create(AgentAssistCallClaimMetrics.MeterName);
        _stale = Meter.CreateCounter<long>(
            ToggleStaleInstrument,
            description: "Agent Assist toggle reads answered with the last known state because the database failed, by path: engine, recognizer.");
    }

    /// <summary>The meter the counter belongs to (tests listen to this instance only).</summary>
    internal Meter Meter { get; }

    /// <summary>Counts one stale answer on <paramref name="path"/>.</summary>
    public void RecordStale(string path) =>
        _stale.Add(1, new KeyValuePair<string, object?>("path", path));

    public void Dispose()
    {
        if (_ownsMeter)
            Meter.Dispose();
    }
}
