using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Push;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The Agent Assist read side across replicas (agent-assist-works-end-to-end, design D12): the engine runs on one replica,
/// while an agent's live stream and a supervisor's Monitor list may be served by any.
/// </summary>
internal static class CrossPodStreamExtensions
{
    /// <summary>
    /// Registers, where the Redis backplane is registered, the receiver that hands the <c>agentassist.*</c> events another
    /// replica published to this replica's live streams, and the Monitor list kept in Redis. Must run before
    /// <see cref="AddAgentAssistReadDefaults"/>, whose registrations then stay out.
    /// </summary>
    public static IServiceCollection AddAgentAssistBackplane(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<CrossPodStreamFeed>();
        services.TryAddSingleton<CrossPodStreamMetrics>();
        services.AddHostedService<CrossPodStreamReceiver>();
        services.TryAddSingleton<IAgentAssistLiveSessionStore>(sp => new RedisAgentAssistLiveSessionStore(
            sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }

    /// <summary>
    /// Registers what the live stream and the Monitor list read when no backplane is configured (one Api replica): a feed
    /// nothing writes to, and the list read from this replica's own sessions.
    /// </summary>
    public static IServiceCollection AddAgentAssistReadDefaults(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<CrossPodStreamFeed>();
        services.TryAddSingleton<IAgentAssistLiveSessionStore>(sp =>
            new InProcessAgentAssistLiveSessionStore(sp.GetRequiredService<IAgentAssistLiveSessions>()));
        return services;
    }
}
