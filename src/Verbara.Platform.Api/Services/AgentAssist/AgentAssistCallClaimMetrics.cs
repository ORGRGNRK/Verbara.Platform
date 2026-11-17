using System.Diagnostics.Metrics;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist per-call claim's counter, for the operator: <c>platform.agentassist.call_claim</c>, tagged
/// <c>outcome</c> (Prometheus <c>platform_agentassist_call_claim_total{outcome}</c>). No call or tenant tag: either
/// would make the series unbounded.
/// </summary>
/// <remarks>
/// Outcomes: <c>claimed</c> (the call is now this replica's), <c>held</c> (another claim stands),
/// <c>error</c> (the claim threw or was cancelled, so it may or may not have been written), <c>released</c> (a
/// release ran; it deletes the row only while the row still carries its claim id) and <c>release_error</c> (a release
/// failed, so a written claim may stay until it is older than the abandon age). Built through an optional
/// <see cref="IMeterFactory"/>; owns its meter.
/// </remarks>
internal sealed class AgentAssistCallClaimMetrics : IDisposable
{
    /// <summary>Name of the <see cref="Meter"/> exported through OpenTelemetry.</summary>
    public const string MeterName = "verbara.platform.agentassist";

    /// <summary>The counter's instrument name.</summary>
    public const string CallClaimInstrument = "platform.agentassist.call_claim";

    public const string Claimed = "claimed";
    public const string Held = "held";
    public const string Error = "error";
    public const string Released = "released";
    public const string ReleaseError = "release_error";

    private readonly Counter<long> _callClaim;

    public AgentAssistCallClaimMetrics(IMeterFactory? meterFactory = null)
    {
        Meter = meterFactory is null ? new Meter(MeterName) : meterFactory.Create(MeterName);
        _callClaim = Meter.CreateCounter<long>(
            CallClaimInstrument,
            description: "Agent Assist per-call claims and releases, by outcome: claimed, held, error, released, release_error.");
    }

    /// <summary>The meter the counter belongs to (tests listen to this instance only).</summary>
    internal Meter Meter { get; }

    /// <summary>Counts one claim or release of <paramref name="outcome"/>.</summary>
    public void Record(string outcome) =>
        _callClaim.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => Meter.Dispose();
}
