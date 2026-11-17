using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Pro.Cluster.Leadership;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.1 (design D4): the activation gate is open only on the voice AMI leader, is
/// closed when no voice AMI leader is registered, and resolves the keyed leader at first use, never at construction.
/// </summary>
public sealed class VoiceLeaderActivationGateTests
{
    [Fact]
    public void IsActive_ShouldBeTrue_WhenThisReplicaHoldsTheVoiceAmiLease()
    {
        using var services = WithLeader(Leader(isLeader: true));

        new VoiceLeaderActivationGate(services).IsActive.Should().BeTrue();
    }

    [Fact]
    public void IsActive_ShouldBeFalse_WhenThisReplicaIsAFollower()
    {
        using var services = WithLeader(Leader(isLeader: false));

        new VoiceLeaderActivationGate(services).IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ShouldBeFalse_WhenNoVoiceAmiLeaderIsRegistered()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        new VoiceLeaderActivationGate(services).IsActive.Should().BeFalse();
    }

    [Fact]
    public void IsActive_ShouldBeFalse_WhenOnlyALeaderForAnotherResourceIsRegistered()
    {
        using var services = new ServiceCollection()
            .AddKeyedSingleton(VoiceLeaderResources.Inbound, Leader(isLeader: true))
            .BuildServiceProvider();

        new VoiceLeaderActivationGate(services).IsActive.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ShouldNotThrow_WhenNoLeaderIsRegistered()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var construct = () => new VoiceLeaderActivationGate(services);

        construct.Should().NotThrow();
    }

    [Fact]
    public void Constructor_ShouldNotResolveTheLeader_UntilIsActiveIsFirstRead()
    {
        var resolutions = 0;
        var leader = Leader(isLeader: true);
        using var services = new ServiceCollection()
            .AddKeyedSingleton<IClusterLeader>(VoiceLeaderResources.AmiOwner, (_, _) =>
            {
                Interlocked.Increment(ref resolutions);
                return leader;
            })
            .BuildServiceProvider();

        var gate = new VoiceLeaderActivationGate(services);

        resolutions.Should().Be(0, "the leader is resolved at first use, not when the gate is built");

        gate.IsActive.Should().BeTrue();
        gate.IsActive.Should().BeTrue();
        resolutions.Should().Be(1);
    }

    [Fact]
    public void IsActive_ShouldFollowTheLease_WhenLeadershipChangesAfterFirstUse()
    {
        var leader = Leader(isLeader: true);
        using var services = WithLeader(leader);
        var gate = new VoiceLeaderActivationGate(services);

        gate.IsActive.Should().BeTrue();

        leader.IsLeader.Returns(false);
        gate.IsActive.Should().BeFalse("the gate reads the lease flag on every read");

        leader.IsLeader.Returns(true);
        gate.IsActive.Should().BeTrue();
    }

    private static ServiceProvider WithLeader(IClusterLeader leader) =>
        new ServiceCollection()
            .AddKeyedSingleton(VoiceLeaderResources.AmiOwner, leader)
            .BuildServiceProvider();

    private static IClusterLeader Leader(bool isLeader)
    {
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        return leader;
    }
}
