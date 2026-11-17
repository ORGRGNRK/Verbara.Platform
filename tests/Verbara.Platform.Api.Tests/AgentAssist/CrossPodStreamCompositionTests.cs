using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using StackExchange.Redis;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Push;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end (design D12): which Monitor list and which live-stream feed each configuration resolves,
/// through the registrations <c>Program.cs</c> makes in its order (the backplane's where Redis is registered, then the read
/// seams). Nothing here connects to Redis.
/// </summary>
public sealed class CrossPodStreamCompositionTests
{
    private static ServiceProvider Compose(bool redis)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddSingleton(Substitute.For<Verbara.Sdk.Push.Bus.IPushEventBus>());
        if (redis)
            services.AddAgentAssistBackplane();
        services.AddAgentAssistReadSeams();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Composition_ShouldShareTheListThroughRedisAndReceiveFromOtherReplicas_WhenRedisIsConfigured()
    {
        using var provider = Compose(redis: true);

        provider.GetRequiredService<IAgentAssistLiveSessionStore>().Should().BeOfType<RedisAgentAssistLiveSessionStore>(
            "every replica reads the list the engine's replica writes");
        provider.GetServices<IHostedService>().OfType<CrossPodStreamReceiver>().Should().ContainSingle(
            "the live streams receive the events another replica published");
        provider.GetRequiredService<CrossPodStreamFeed>().Should().NotBeNull();
    }

    [Fact]
    public void Composition_ShouldReadThisReplicasSessionsAndReceiveNothing_WhenNoRedisIsConfigured()
    {
        using var provider = Compose(redis: false);

        provider.GetRequiredService<IAgentAssistLiveSessionStore>().Should().BeOfType<InProcessAgentAssistLiveSessionStore>(
            "with no backplane there is one replica, and the list is its own");
        provider.GetServices<IHostedService>().OfType<CrossPodStreamReceiver>().Should().BeEmpty();
        provider.GetRequiredService<CrossPodStreamFeed>().HasSubscribers.Should().BeFalse("the stream's feed resolves, empty");
    }
}
