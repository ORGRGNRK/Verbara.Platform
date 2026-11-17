using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Whisper;
using Xunit;

namespace Verbara.Platform.Api.Tests.Speech;

public sealed class SpeechRecognizerFactoryTests
{
    private const string ApiKey = "sk-factory-test-key-7f3a";
    private const string UserInfoSecret = "userinfo-secret-91c2";
    private const string QuerySecret = "query-secret-4d8e";

    [Fact]
    public async Task Create_ShouldBuildWhisperRecognizerWithGivenEndpointKeyAndLanguage_WhenProviderIsWhisper()
    {
        var handler = new CapturingHandler("""{"text":"olá"}""");
        var httpClients = HttpClientsReturning(handler, "qa-stt");
        var factory = new SpeechRecognizerFactory(httpClients, NullLogger<SpeechRecognizerFactory>.Instance);
        var endpoint = new Uri("http://stt.internal.test:9000/v1/audio/transcriptions");

        await using var recognizer = factory.Create(
            new SpeechRecognizerSettings
            {
                ProviderId = "whisper",
                Language = "pt",
                ApiKey = ApiKey,
                Endpoint = endpoint,
                Model = "large-v3",
            },
            "qa-stt");

        recognizer.Should().BeOfType<WhisperSpeechRecognizer>();
        var results = await DrainAsync(recognizer);

        httpClients.Received(1).CreateClient("qa-stt");
        handler.RequestUri.Should().Be(endpoint);
        handler.Authorization.Should().Be($"Bearer {ApiKey}");
        handler.FormFields.Should().Contain("language", "pt").And.Contain("model", "large-v3");
        results.Should().ContainSingle().Which.Transcript.Should().Be("olá");
    }

    [Fact]
    public async Task Create_ShouldKeepTheSdkDefaultModel_WhenNoModelIsGiven()
    {
        var handler = new CapturingHandler("""{"text":"hola"}""");
        var factory = new SpeechRecognizerFactory(
            HttpClientsReturning(handler, "qa-stt"), NullLogger<SpeechRecognizerFactory>.Instance);

        await using var recognizer = factory.Create(
            new SpeechRecognizerSettings
            {
                ProviderId = "whisper",
                Language = "es",
                ApiKey = ApiKey,
                Endpoint = new Uri("http://stt.internal.test:9000/v1/audio/transcriptions"),
            },
            "qa-stt");
        await DrainAsync(recognizer);

        handler.FormFields.Should().Contain("model", new WhisperOptions().Model).And.Contain("language", "es");
    }

    [Theory]
    [InlineData("Deepgram")]
    [InlineData("azure-whisper")]
    [InlineData("google")]
    [InlineData("Whisper")]
    [InlineData("not-a-provider")]
    public void Create_ShouldRefuseWithMessageNamingTheProvider_WhenProviderIsUnmapped(string providerId)
    {
        var httpClients = Substitute.For<IHttpClientFactory>();
        var factory = new SpeechRecognizerFactory(httpClients, NullLogger<SpeechRecognizerFactory>.Instance);

        var act = () => factory.Create(
            new SpeechRecognizerSettings { ProviderId = providerId, Language = "es", ApiKey = ApiKey }, "qa-stt");

        act.Should().Throw<ArgumentException>()
            .Which.Message.Should().Contain($"'{providerId}'").And.NotContain(ApiKey);
        httpClients.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    [Fact]
    public void Create_ShouldRefuse_WhenWhisperHasNoNamedHttpClient()
    {
        var factory = new SpeechRecognizerFactory(
            Substitute.For<IHttpClientFactory>(), NullLogger<SpeechRecognizerFactory>.Instance);

        var act = () => factory.Create(
            new SpeechRecognizerSettings { ProviderId = "whisper", Language = "es", ApiKey = ApiKey }, httpClientName: null);

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("'whisper'").And.Contain("HttpClient");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Create_ShouldRefuse_WhenLanguageIsBlank(string language)
    {
        var factory = new SpeechRecognizerFactory(
            Substitute.For<IHttpClientFactory>(), NullLogger<SpeechRecognizerFactory>.Instance);

        var act = () => factory.Create(
            new SpeechRecognizerSettings { ProviderId = "whisper", Language = language, ApiKey = ApiKey }, "qa-stt");

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("language");
    }

    [Fact]
    public async Task Create_ShouldWriteNoCredentialValueToAnyLogLine_WhenBuildingAndRefusing()
    {
        using var capture = new LogRecordCapture();
        using var loggerFactory = LoggerFactory.Create(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddProvider(capture));
        var factory = new SpeechRecognizerFactory(
            HttpClientsReturning(new CapturingHandler("""{"text":"x"}"""), "qa-stt"),
            loggerFactory.CreateLogger<SpeechRecognizerFactory>());
        var endpoint = new Uri($"https://stt-user:{UserInfoSecret}@stt.internal.test/v1/audio/transcriptions?key={QuerySecret}");

        await using var built = factory.Create(
            new SpeechRecognizerSettings { ProviderId = "whisper", Language = "es", ApiKey = ApiKey, Endpoint = endpoint },
            "qa-stt");
        var refused = Record.Exception(() => factory.Create(
            new SpeechRecognizerSettings { ProviderId = "google", Language = "es", ApiKey = ApiKey, Endpoint = endpoint },
            "qa-stt"));

        capture.Records.Should().NotBeEmpty("the factory logs what it builds and what it refuses");
        capture.Records.Should().Contain(record => record.Contains("stt.internal.test"));
        foreach (var secret in new[] { ApiKey, UserInfoSecret, QuerySecret })
        {
            capture.RecordsContaining(secret).Should().BeEmpty($"'{secret}' is a credential");
            refused!.Message.Should().NotContain(secret);
        }
    }

    [Fact]
    public void SettingsToString_ShouldOmitCredentials_WhenAKeyAndAnEndpointWithUserInfoAreSet()
    {
        var settings = new SpeechRecognizerSettings
        {
            ProviderId = "whisper",
            Language = "es",
            ApiKey = ApiKey,
            Endpoint = new Uri($"https://stt-user:{UserInfoSecret}@stt.internal.test/v1?key={QuerySecret}"),
        };

        settings.ToString().Should().Contain("whisper").And.Contain("stt.internal.test")
            .And.NotContain(ApiKey).And.NotContain(UserInfoSecret).And.NotContain(QuerySecret);
    }

    [Fact]
    public void TryGetMode_ShouldReportWhisperAsBatchDeepgramAsStreamingAndOnlyMappedProviders_WhenAsked()
    {
        SpeechRecognizerFactory.TryGetMode("whisper", out var mode).Should().BeTrue();
        mode.Should().Be(SpeechRecognizerMode.Batch);
        SpeechRecognizerFactory.TryGetMode("deepgram", out mode).Should().BeTrue();
        mode.Should().Be(SpeechRecognizerMode.Streaming);
        SpeechRecognizerFactory.TryGetMode("google", out _).Should().BeFalse();
        SpeechRecognizerFactory.MappedProviders.Should().BeEquivalentTo(["whisper", "deepgram"]);
    }

    [Fact]
    public void AddSpeechRecognizerFactory_ShouldResolveOneSingleton_WhenTheProviderValidatesOnBuild()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSpeechRecognizerFactory();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetRequiredService<SpeechRecognizerFactory>()
            .Should().BeSameAs(provider.GetRequiredService<SpeechRecognizerFactory>())
            .And.BeSameAs(provider.GetRequiredService<ISpeechRecognizerFactory>());
    }

    // ---------------------------------------------------------------- deepgram (agent-assist-works-end-to-end B.2)

    [Fact]
    public async Task Create_ShouldOpenDeepgramAtTheGivenEndpointWithKeyLanguageAndModel_WhenProviderIsDeepgram()
    {
        await using var server = await UpgradeCapture.StartAsync();
        var httpClients = Substitute.For<IHttpClientFactory>();
        var factory = new SpeechRecognizerFactory(httpClients, NullLogger<SpeechRecognizerFactory>.Instance);

        await using var recognizer = factory.Create(
            new SpeechRecognizerSettings
            {
                ProviderId = "deepgram",
                Language = "pt",
                ApiKey = ApiKey,
                Endpoint = new Uri($"ws://127.0.0.1:{server.Port}/v1/listen"),
                Model = "nova-3",
            },
            "agentassist-stt");

        recognizer.Should().BeOfType<DeepgramSpeechRecognizer>();
        var act = async () => await DrainAsync(recognizer);
        await act.Should().ThrowAsync<SpeechProviderFailureException>("the capture answers the upgrade with 401");

        var request = await server.Request;
        request.Target.Should().StartWith("/v1/listen?");
        request.Target.Should().Contain("language=pt").And.Contain("model=nova-3");
        request.Authorization.Should().Be($"Token {ApiKey}");
        httpClients.DidNotReceiveWithAnyArgs().CreateClient(default!);
    }

    [Fact]
    public async Task Create_ShouldOpenDeepgramsListenRoute_WhenTheEndpointNamesAHostOnly()
    {
        await using var server = await UpgradeCapture.StartAsync();
        var factory = new SpeechRecognizerFactory(
            Substitute.For<IHttpClientFactory>(), NullLogger<SpeechRecognizerFactory>.Instance);

        await using var recognizer = factory.Create(new SpeechRecognizerSettings
        {
            ProviderId = "deepgram",
            Language = "es",
            ApiKey = ApiKey,
            Endpoint = new Uri($"ws://127.0.0.1:{server.Port}"),
        });
        var act = async () => await DrainAsync(recognizer);
        await act.Should().ThrowAsync<SpeechProviderFailureException>();

        (await server.Request).Target.Should().StartWith("/v1/listen?", "a host-only endpoint gets Deepgram's live route");
    }

    [Fact]
    public void DeepgramOptionsFor_ShouldKeepTheSdkDefaults_WhenNoEndpointAndNoModelAreGiven()
    {
        var options = SpeechRecognizerFactory.DeepgramOptionsFor(
            new SpeechRecognizerSettings { ProviderId = "deepgram", Language = "es", ApiKey = ApiKey });

        options.BaseUri.Should().Be(new DeepgramOptions().BaseUri);
        options.Model.Should().Be(new DeepgramOptions().Model);
        options.ApiKey.Should().Be(ApiKey);
        options.Language.Should().Be("es");
    }

    [Theory]
    [InlineData("wss://api.deepgram.com", "wss://api.deepgram.com/v1/listen")]
    [InlineData("wss://api.deepgram.com/", "wss://api.deepgram.com/v1/listen")]
    [InlineData("wss://stt.internal.test:8443/v1/listen", "wss://stt.internal.test:8443/v1/listen")]
    [InlineData("ws://stt.internal.test/custom/route?x=1", "ws://stt.internal.test/custom/route")]
    public void DeepgramOptionsFor_ShouldUseTheEndpointAsBaseUri_WhenOneIsGiven(string endpoint, string expected)
    {
        var options = SpeechRecognizerFactory.DeepgramOptionsFor(new SpeechRecognizerSettings
        {
            ProviderId = "deepgram", Language = "es", ApiKey = ApiKey, Endpoint = new Uri(endpoint),
        });

        options.BaseUri.Should().Be(expected);
    }

    [Fact]
    public void Create_ShouldRefuseWithoutNamingTheEndpoint_WhenADeepgramEndpointIsNotAWebSocketUri()
    {
        var factory = new SpeechRecognizerFactory(
            Substitute.For<IHttpClientFactory>(), NullLogger<SpeechRecognizerFactory>.Instance);

        var act = () => factory.Create(new SpeechRecognizerSettings
        {
            ProviderId = "deepgram",
            Language = "es",
            ApiKey = ApiKey,
            Endpoint = new Uri($"https://stt-user:{UserInfoSecret}@stt.internal.test/v1/listen"),
        });

        act.Should().Throw<ArgumentException>().Which.Message.Should().Contain("ws://")
            .And.NotContain(UserInfoSecret).And.NotContain("stt.internal.test");
    }

    private static IHttpClientFactory HttpClientsReturning(HttpMessageHandler handler, string name)
    {
        var httpClients = Substitute.For<IHttpClientFactory>();
        httpClients.CreateClient(name).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return httpClients;
    }

    private static async Task<List<SpeechRecognitionResult>> DrainAsync(SpeechRecognizer recognizer)
    {
        var results = new List<SpeechRecognitionResult>();
        await foreach (var result in recognizer.StreamAsync(OneFrame(), AudioFormat.Slin16Mono8kHz))
            results.Add(result);
        return results;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> OneFrame()
    {
        await Task.Yield();
        yield return new byte[320];
    }

    /// <summary>
    /// A TCP listener that reads one HTTP upgrade request, keeps its request target and <c>Authorization</c> header and
    /// answers 401, so a WebSocket client's real handshake can be observed without a provider.
    /// </summary>
    private sealed class UpgradeCapture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task<CapturedUpgrade> _request;

        private UpgradeCapture(TcpListener listener)
        {
            _listener = listener;
            _request = AcceptOneAsync(listener);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<CapturedUpgrade> Request => _request.WaitAsync(TimeSpan.FromSeconds(15));

        public static Task<UpgradeCapture> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new UpgradeCapture(listener));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            _listener.Dispose();
            try
            {
                await _request.ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // The listener was stopped before any client connected.
            }
            catch (ObjectDisposedException)
            {
                // The listener was disposed before any client connected.
            }
        }

        private static async Task<CapturedUpgrade> AcceptOneAsync(TcpListener listener)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync() ?? string.Empty;
            string? authorization = null;
            while (await reader.ReadLineAsync() is { Length: > 0 } header)
            {
                if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                    authorization = header["Authorization:".Length..].Trim();
            }

            var reply = Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply);
            var parts = requestLine.Split(' ');
            return new CapturedUpgrade(parts.Length > 1 ? parts[1] : string.Empty, authorization);
        }
    }

    private sealed record CapturedUpgrade(string Target, string? Authorization);

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? Authorization { get; private set; }

        public Dictionary<string, string> FormFields { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var name = part.Headers.ContentDisposition?.Name?.Trim('"');
                    if (name is not null && part is StringContent)
                        FormFields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
