using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Pro.AgentAssist.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.Cluster.Leadership;
using CoreAgentAssistOptions = Verbara.Platform.Core.Configuration.AgentAssistOptions;
using SnoopOptions = Verbara.Sdk.Pro.AgentAssist.Options.AgentAssistOptions;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The Agent Assist composition of the Api: the feature toggle every replica shares, and the engine with the host seams
/// it reads (the bridge to the event bus, the activation gate, the per-call claim, the snoop settings and the
/// recognizer each session builds).
/// </summary>
internal static class AgentAssistHostingExtensions
{
    /// <summary>
    /// Registers the feature toggle the admin page, the engine and each session read: the <c>AgentAssist</c> options, the
    /// credentials protector and <see cref="IAgentAssistFeatureStore"/>, forwarded as <see cref="IAgentAssistFeatureToggle"/>.
    /// With the core database the store is <see cref="PostgresAgentAssistFeatureToggle"/>, one state for every replica;
    /// without it, <see cref="InMemoryAgentAssistFeatureToggle"/>, one replica only. Registered whether or not the engine
    /// is, so the admin routes always resolve.
    /// </summary>
    public static IServiceCollection AddAgentAssistFeatureStore(
        this IServiceCollection services, IConfiguration configuration, NpgsqlDataSource? coreDataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CoreAgentAssistOptions>(configuration.GetSection("AgentAssist"));
        services.AddSingleton<AgentAssistCredentialsProtector>();
        if (coreDataSource is null)
        {
            services.AddSingleton<IAgentAssistFeatureStore, InMemoryAgentAssistFeatureToggle>();
        }
        else
        {
            services.TryAddSingleton<AgentAssistToggleMetrics>();
            services.AddSingleton<IAgentAssistFeatureStore>(sp => new PostgresAgentAssistFeatureToggle(
                coreDataSource,
                sp.GetRequiredService<IOptions<CoreAgentAssistOptions>>(),
                sp.GetRequiredService<AgentAssistCredentialsProtector>(),
                sp.GetRequiredService<AgentAssistToggleMetrics>(),
                sp.GetRequiredService<ILogger<PostgresAgentAssistFeatureToggle>>()));
        }

        services.AddSingleton<IAgentAssistFeatureToggle>(sp => sp.GetRequiredService<IAgentAssistFeatureStore>());
        return services;
    }

    /// <summary>
    /// Registers the Agent Assist engine (Pro <c>AddProAgentAssist</c>) with Platform's seams. Called where the engine's
    /// stores exist (an Analytics, Dialer or core connection string).
    /// </summary>
    /// <param name="services">The host's services; <see cref="AddAgentAssistFeatureStore"/> must have run.</param>
    /// <param name="configuration">Reads <c>AgentAssist:AudioSocket:Port</c>.</param>
    /// <param name="snoopApplication">
    /// The Stasis app this replica's node ARI client registers (<see cref="StasisAppNames.NodeInstance"/>); the engine
    /// creates its snoops there, so a snoop is always born in an app this replica holds.
    /// </param>
    /// <param name="coreDataSource">The core database, which holds the per-call claims; <see langword="null"/>: no claim.</param>
    public static IServiceCollection AddPlatformAgentAssist(
        this IServiceCollection services, IConfiguration configuration, string snoopApplication,
        NpgsqlDataSource? coreDataSource)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(snoopApplication);

        // Before the engine: the host starts hosted services in registration order, so the bridge has subscribed to the
        // supervisor's session announcements (no replay) before the engine can announce any.
        services.AddHostedService<AgentAssistBridge>();

        services.AddAgentAssistRecognizer();
        services.TryAddSingleton<IAgentAssistActivationGate, VoiceLeaderActivationGate>();
        services.TryAddSingleton(sp => new AgentAssistAdvertisedAddress(
            sp.GetRequiredService<SnoopOptions>(),
            sp.GetRequiredService<IOptions<CoreAgentAssistOptions>>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ILogger<AgentAssistAdvertisedAddress>>()));
        services.TryAddSingleton(sp => new AgentAssistEngineToggle(
            sp.GetRequiredService<IAgentAssistFeatureToggle>(), sp.GetRequiredService<AgentAssistAdvertisedAddress>()));
        if (coreDataSource is not null)
        {
            services.TryAddSingleton<AgentAssistCallClaimMetrics>();
            services.TryAddSingleton<IAgentAssistCallClaim>(sp => new PostgresAgentAssistCallClaim(
                coreDataSource,
                sp.GetRequiredService<IOptions<ClusterLeadershipOptions>>().Value.InstanceId,
                sp.GetRequiredService<AgentAssistCallClaimMetrics>(),
                sp.GetRequiredService<ILogger<PostgresAgentAssistCallClaim>>()));
        }

        var audioSocketPort = configuration.GetValue("AgentAssist:AudioSocket:Port", 9093);
        services.AddProAgentAssist(assist =>
        {
            assist
                .WithRuntimeFeatureToggle(sp => sp.GetRequiredService<AgentAssistEngineToggle>())
                .WithActivationGate(sp => sp.GetRequiredService<IAgentAssistActivationGate>())
                .WithSnoop(snoop =>
                {
                    snoop.SnoopApplication = snoopApplication;
                    snoop.AudioSocketPort = audioSocketPort;
                    // AudioSocketAdvertisedAddress stays unset here: AgentAssistAdvertisedAddress sets it at first use.
                })
                .WithSpeechRecognizer<ToggleBackedSpeechRecognizer>();
            if (coreDataSource is not null)
                assist.WithCallClaim(sp => sp.GetRequiredService<IAgentAssistCallClaim>());
        });
        return services;
    }
}
