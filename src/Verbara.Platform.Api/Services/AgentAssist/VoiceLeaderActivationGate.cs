using Microsoft.Extensions.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Agent Assist engine's activation gate: this replica acts on connected calls only while it holds the voice AMI
/// lease (<see cref="VoiceLeaderResources.AmiOwner"/>), the replica that also stamps the call's tenant.
/// </summary>
/// <remarks>
/// <para>
/// The keyed <see cref="IClusterLeader"/> is resolved at first use, never in the constructor: it is registered only
/// when voice AMI is configured, and resolving it at host start would fail a boot without that configuration. When no
/// leader is registered the gate is inactive: a replica with no voice AMI never starts a session.
/// </para>
/// <para>
/// <see cref="IsActive"/> reads the leader's lease flag, which is cheap, non-blocking and safe from several threads.
/// It is a pre-check only: two replicas can read it true at once (an old holder whose renewal is pending, or a paused
/// process), so exclusivity per call belongs to the per-call claim, not to this gate.
/// </para>
/// </remarks>
internal sealed class VoiceLeaderActivationGate : IAgentAssistActivationGate
{
    private readonly Lazy<IClusterLeader?> _leader;

    public VoiceLeaderActivationGate(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _leader = new Lazy<IClusterLeader?>(
            () => services.GetKeyedService<IClusterLeader>(VoiceLeaderResources.AmiOwner),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// <see langword="true"/> while this replica holds the voice AMI lease; <see langword="false"/> otherwise, and
    /// always when no voice AMI leader is registered.
    /// </summary>
    public bool IsActive => _leader.Value?.IsLeader ?? false;
}
