using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.VoiceAi;
using SnoopOptions = Verbara.Sdk.Pro.AgentAssist.Options.AgentAssistOptions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end, task B.4 (design D1, D3-D7): what the Api's Agent Assist composition registers, through
/// the two extensions <c>Program.cs</c> calls, for a host with and without the core database. The data source is built
/// over an address nothing listens on; nothing here opens a connection.
/// </summary>
public sealed class AgentAssistHostingExtensionsTests
{
    private const string SnoopApp = "verbara-node-node1";
    private const string UnreachableCore = "Host=127.0.0.1;Port=1;Database=core;Username=u;Password=p";

    [Fact]
    public void AddPlatformAgentAssist_ShouldClaimEachCallInTheCoreDatabase_WhenTheCoreDataSourceExists()
    {
        using var core = NpgsqlDataSource.Create(UnreachableCore);
        using var provider = Compose(core);

        provider.GetService<IAgentAssistCallClaim>().Should().BeOfType<PostgresAgentAssistCallClaim>(
            "with the core database every replica's engine claims a call in the same table, so one session runs per call");
        provider.GetRequiredService<IAgentAssistFeatureStore>().Should().BeOfType<PostgresAgentAssistFeatureToggle>(
            "with the core database the toggle is one state for every replica");
    }

    [Fact]
    public void AddPlatformAgentAssist_ShouldTakeNoClaimAndKeepTheToggleInMemory_WhenThereIsNoCoreDataSource()
    {
        using var provider = Compose(core: null);

        provider.GetService<IAgentAssistCallClaim>().Should().BeNull("without the core database there is no claim table");
        provider.GetRequiredService<IAgentAssistFeatureStore>().Should().BeOfType<InMemoryAgentAssistFeatureToggle>();
        provider.GetRequiredService<IAgentAssistFeatureToggle>().Should().BeSameAs(
            provider.GetRequiredService<IAgentAssistFeatureStore>(), "the admin page and the engine read one store");
    }

    [Fact]
    public void AddPlatformAgentAssist_ShouldWireTheGateTheRecognizerAndTheSnoops()
    {
        var services = Services(core: null, port: "9100");
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAgentAssistActivationGate>().Should().BeOfType<VoiceLeaderActivationGate>();
        services.Should().Contain(d => d.ServiceType == typeof(SpeechRecognizer)
            && d.ImplementationType == typeof(ToggleBackedSpeechRecognizer) && d.Lifetime == ServiceLifetime.Transient);
        services.Should().Contain(d => d.ServiceType == typeof(AgentAssistRecognizerSession)
            && d.Lifetime == ServiceLifetime.Scoped);

        var snoop = provider.GetRequiredService<SnoopOptions>();
        snoop.SnoopApplication.Should().Be(SnoopApp, "a snoop is created in this replica's node app");
        snoop.AudioSocketPort.Should().Be(9100);
        snoop.AudioSocketAdvertisedAddress.Should().BeNull("the address is resolved at first use, never at registration");
    }

    [Fact]
    public void AddPlatformAgentAssist_ShouldRegisterTheBridgeBeforeTheEngine()
    {
        var services = Services(core: null);

        var bridge = services.ToList().FindIndex(d =>
            d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentAssistBridge));
        var engineType = typeof(Verbara.Sdk.Pro.AgentAssist.Engine.AgentAssistSupervisor).Assembly.GetType(
            "Verbara.Sdk.Pro.AgentAssist.Engine.AgentAssistEngine", throwOnError: true);
        var engine = services.ToList().FindIndex(d => d.ServiceType == engineType);

        bridge.Should().BeGreaterThanOrEqualTo(0, "the bridge is hosted where the engine is");
        engine.Should().BeGreaterThan(bridge,
            "the host starts hosted services in registration order, so the bridge subscribes before the engine can announce");
    }

    private static ServiceProvider Compose(NpgsqlDataSource? core) => Services(core).BuildServiceProvider();

    private static ServiceCollection Services(NpgsqlDataSource? core, string? port = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentAssist:AudioSocket:Port"] = port,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAgentAssistFeatureStore(configuration, core);
        services.AddPlatformAgentAssist(configuration, SnoopApp, core);
        return services;
    }
}
