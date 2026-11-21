using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Pro.AgentAssist.Features;
using SnoopOptions = Verbara.Sdk.Pro.AgentAssist.Options.AgentAssistOptions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end, task B.4 (design D3): the address each snoop's audio is sent to comes from
/// <c>AgentAssist:AudioSocket:AdvertisedHost</c> and <c>:Port</c>, is resolved at first use (never at host start), and a
/// missing or unusable host leaves it unset without throwing, so each call is skipped and the boot is unaffected.
/// </summary>
public sealed class AgentAssistAdvertisedAddressTests
{
    [Theory]
    [InlineData("10.0.0.7", 9093, "10.0.0.7:9093")]
    [InlineData("pbx-media.internal", 9100, "pbx-media.internal:9100")]
    [InlineData("fd00::7", 9093, "[fd00::7]:9093")]
    [InlineData("[fd00::7]", 9093, "[fd00::7]:9093")]
    [InlineData("two words", 9093, null)]
    [InlineData("host:1234", 9093, null)]
    [InlineData("10.0.0.7", 0, null)]
    public void Format_ShouldWriteHostAndPort_AsTheEngineAcceptsIt(string host, int port, string? expected)
    {
        AgentAssistAdvertisedAddress.Format(host, port).Should().Be(expected);
    }

    [Fact]
    public async Task EnsureAsync_ShouldSetTheLiteralHost_AtFirstUseOnly()
    {
        var snoop = new SnoopOptions();
        using var address = Address(snoop, "10.0.0.7", ariBaseUrl: null);

        snoop.AudioSocketAdvertisedAddress.Should().BeNull("nothing is resolved when the resolver is built");
        await address.EnsureAsync(CancellationToken.None);

        snoop.AudioSocketAdvertisedAddress.Should().Be("10.0.0.7:9093");
    }

    [Fact]
    public async Task EnsureAsync_ShouldAdvertiseTheLocalAddressOfTheRouteToAsterisk_WhenTheHostIsAuto()
    {
        var snoop = new SnoopOptions();
        using var address = Address(snoop, "auto", ariBaseUrl: "http://127.0.0.1:8088");

        await address.EnsureAsync(CancellationToken.None);

        snoop.AudioSocketAdvertisedAddress.Should().Be("127.0.0.1:9093",
            "the route to a loopback ARI host leaves through the loopback address");
    }

    [Theory]
    [InlineData(null, "http://127.0.0.1:8088")]
    [InlineData("auto", null)]
    [InlineData("two words", null)]
    public async Task EnsureAsync_ShouldLeaveTheAddressUnsetWithoutThrowing_WhenNoUsableHostIsConfigured(
        string? host, string? ariBaseUrl)
    {
        var snoop = new SnoopOptions();
        using var address = Address(snoop, host, ariBaseUrl);

        var ensure = async () => await address.EnsureAsync(CancellationToken.None);

        await ensure.Should().NotThrowAsync();
        snoop.AudioSocketAdvertisedAddress.Should().BeNull("the engine then skips each call and counts it");
    }

    [Fact]
    public async Task EngineToggle_ShouldResolveTheAddress_OnlyWhileTheFeatureIsOn()
    {
        var snoop = new SnoopOptions();
        using var address = Address(snoop, "10.0.0.7", ariBaseUrl: null);
        var store = new CountingFeatureStore(AgentAssistFeatureState.Disabled);
        var toggle = new AgentAssistEngineToggle(store, address);

        (await toggle.GetCurrentAsync()).Enabled.Should().BeFalse();
        snoop.AudioSocketAdvertisedAddress.Should().BeNull("a call the engine will not assist needs no address");

        store.State = new AgentAssistFeatureState(true, "deepgram", null, null, null);
        (await toggle.GetCurrentAsync()).Enabled.Should().BeTrue();
        snoop.AudioSocketAdvertisedAddress.Should().Be("10.0.0.7:9093",
            "the engine reads the toggle before the address, so the first assisted call finds it set");
    }

    private static AgentAssistAdvertisedAddress Address(SnoopOptions snoop, string? host, string? ariBaseUrl)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Asterisk:Ari:BaseUrl"] = ariBaseUrl,
        }).Build();
        return new AgentAssistAdvertisedAddress(
            snoop, Options.Create(Platform(host)), configuration, NullLogger<AgentAssistAdvertisedAddress>.Instance);
    }

    private static AgentAssistOptions Platform(string? host) => new()
    {
        AudioSocket = new AgentAssistAudioSocketOptions { AdvertisedHost = host, Port = 9093 },
    };
}
