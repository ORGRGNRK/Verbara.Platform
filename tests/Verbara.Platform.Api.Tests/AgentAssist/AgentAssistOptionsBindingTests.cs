using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Verbara.Platform.Core.Configuration;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task A.3 (design D3, D5): the Agent Assist keys the host reads for the snoop audio
/// destination (<c>AgentAssist:AudioSocket:Port</c>, <c>AgentAssist:AudioSocket:AdvertisedHost</c>) and for the
/// Deepgram recognizer (<c>AgentAssist:Deepgram:Model</c>, <c>AgentAssist:Deepgram:Language</c>), their defaults, and
/// their binding from the <c>AgentAssist</c> section through the real <c>Program</c> (the configuration binding
/// source generator, the AOT path).
/// </summary>
public sealed class AgentAssistOptionsBindingTests
{
    [Fact]
    public void AgentAssistOptions_ShouldDefaultToPort9093AndNoHostModelOrLanguage_WhenNothingIsConfigured()
    {
        var options = new AgentAssistOptions();

        options.AudioSocket.Port.Should().Be(9093);
        options.AudioSocket.AdvertisedHost.Should().BeNull("with no advertised host no snoop is created");
        options.Deepgram.Model.Should().BeNull();
        options.Deepgram.Language.Should().BeNull();
        AgentAssistAudioSocketOptions.AutoAdvertisedHost.Should().Be("auto");
    }

    [Fact]
    public void Host_ShouldBindTheAudioSocketAndDeepgramKeys_WhenTheAgentAssistSectionSetsThem()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["AgentAssist:AudioSocket:Port"] = "9193",
            ["AgentAssist:AudioSocket:AdvertisedHost"] = "10.0.0.7",
            ["AgentAssist:Deepgram:Model"] = "nova-3",
            ["AgentAssist:Deepgram:Language"] = "es-419",
        });

        var options = factory.Services.GetRequiredService<IOptions<AgentAssistOptions>>().Value;

        options.AudioSocket.Port.Should().Be(9193);
        options.AudioSocket.AdvertisedHost.Should().Be("10.0.0.7");
        options.Deepgram.Model.Should().Be("nova-3");
        options.Deepgram.Language.Should().Be("es-419");
    }

    [Fact]
    public void Host_ShouldKeepAutoAsTheAdvertisedHost_WhenTheKeyIsAuto()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["AgentAssist:AudioSocket:AdvertisedHost"] = "auto",
        });

        var options = factory.Services.GetRequiredService<IOptions<AgentAssistOptions>>().Value;

        options.AudioSocket.AdvertisedHost.Should().Be(AgentAssistAudioSocketOptions.AutoAdvertisedHost,
            "auto is resolved per replica at first use, never at binding");
        options.AudioSocket.Port.Should().Be(9093);
    }

    [Fact]
    public void Host_ShouldStillBindTheDeepgramSeedCredentials_WhenTheModelAndLanguageAreSetBesideThem()
    {
        using var factory = Factory(new Dictionary<string, string?>
        {
            ["AgentAssist:Deepgram:ApiKey"] = "dg-seed-key",
            ["AgentAssist:Deepgram:Endpoint"] = "wss://stt.example.test/v1/listen",
            ["AgentAssist:Deepgram:Language"] = "en-US",
        });

        var options = factory.Services.GetRequiredService<IOptions<AgentAssistOptions>>().Value;

        options.Deepgram.ApiKey.Should().Be("dg-seed-key");
        options.Deepgram.Endpoint.Should().Be("wss://stt.example.test/v1/listen");
        options.Deepgram.Language.Should().Be("en-US");
        options.Deepgram.Model.Should().BeNull("the model is optional");
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory(
        IReadOnlyDictionary<string, string?> settings) =>
        new EntryPointStartPlatformApiFactory().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        });
}
