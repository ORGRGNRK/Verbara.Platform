using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.2 (design D5): a session's two <see cref="ToggleBackedSpeechRecognizer"/>s
/// share one <see cref="AgentAssistRecognizerSession"/>, which reads the stored state once, decrypts it once and
/// decides once whether the session transcribes; the inner recognizer comes from the shared factory and is disposed
/// when its stream ends.
/// </summary>
public sealed class ToggleBackedSpeechRecognizerTests : IDisposable
{
    private const string Key = "dg-session-key-61c0aa";
    private const string OtherKey = "dg-session-key-other-0f19";
    private const string Admin = "0651f84b6f284a1c9b8d658330fa8310";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    private readonly AgentAssistCredentialsProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly AgentAssistRecognizerMetrics _metrics = new();
    private readonly UnavailableCounts _counts;
    private readonly LogRecordCapture _logs = new();
    private readonly ILoggerFactory _loggerFactory;

    public ToggleBackedSpeechRecognizerTests()
    {
        _counts = new UnavailableCounts(_metrics);
        _loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(_logs));
    }

    public void Dispose()
    {
        _counts.Dispose();
        _metrics.Dispose();
        _loggerFactory.Dispose();
    }

    // ---------------------------------------------------------------- deepgram

    [Fact]
    public async Task StreamAsync_ShouldBuildDeepgramWithTheStoredKeyEndpointAndConfiguredLanguage_WhenAnEndpointIsStored()
    {
        var store = new CountingFeatureStore(Deepgram(Key, "wss://stt.internal.test/v1/listen"));
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(store, factory, language: "pt", model: "nova-3");

        var results = await DrainAsync(NewWrapper(session), frames: 3);

        results.Should().BeEmpty();
        var (settings, client) = factory.Calls.Should().ContainSingle().Subject;
        settings.ProviderId.Should().Be("deepgram");
        settings.ApiKey.Should().Be(Key);
        settings.Endpoint.Should().Be(new Uri("wss://stt.internal.test/v1/listen"));
        settings.Language.Should().Be("pt");
        settings.Model.Should().Be("nova-3");
        client.Should().Be(AgentAssistRecognizerSession.HttpClientName);
        factory.Built.Single().FramesRead.Should().Be(3, "the inner recognizer receives the stream's audio");
        _counts.Total.Should().Be(0);
    }

    [Fact]
    public async Task StreamAsync_ShouldBuildDeepgramWithTheProvidersDefaultEndpoint_WhenNoEndpointIsStored()
    {
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(new CountingFeatureStore(Deepgram(Key)), factory);

        await DrainAsync(NewWrapper(session));

        var settings = factory.Calls.Should().ContainSingle().Subject.Settings;
        settings.Endpoint.Should().BeNull();
        settings.Model.Should().BeNull("no model is configured, so the recognizer keeps its default");
        settings.ApiKey.Should().Be(Key);
    }

    [Fact]
    public async Task CreateRecognizer_ShouldBuildARealDeepgramRecognizerThroughTheSharedFactory_WhenTheStateIsLive()
    {
        var factory = new SpeechRecognizerFactory(
            Substitute.For<IHttpClientFactory>(), _loggerFactory.CreateLogger<SpeechRecognizerFactory>());
        var session = NewSession(new CountingFeatureStore(Deepgram(Key, "wss://stt.internal.test")), factory);

        var settings = await session.GetSettingsAsync(CancellationToken.None);
        await using var recognizer = session.CreateRecognizer(settings!);

        recognizer.Should().BeOfType<DeepgramSpeechRecognizer>();
        SpeechRecognizerFactory.DeepgramOptionsFor(settings!).BaseUri.Should().Be("wss://stt.internal.test/v1/listen");
        _logs.RecordsContaining(Key).Should().BeEmpty("no credential value reaches a log line");
    }

    // ---------------------------------------------------------------- unavailable

    [Theory]
    [InlineData("whisper")]
    [InlineData("azure-whisper")]
    [InlineData("google")]
    public async Task StreamAsync_ShouldYieldNothingAndCountProviderNotLive_WhenABatchProviderIsStored(string provider)
    {
        var store = new CountingFeatureStore(Stored(provider, Key));
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(store, factory);
        var frames = new FrameSource(4);

        var results = await DrainAsync(NewWrapper(session), frames);

        results.Should().BeEmpty();
        frames.Read.Should().Be(4, "the session's audio is read to the end and dropped");
        factory.Calls.Should().BeEmpty("no recognizer is built for a provider that cannot transcribe a live call");
        _counts.Get(AgentAssistRecognizerMetrics.ProviderNotLive).Should().Be(1);
        _counts.Total.Should().Be(1);
        Warnings().Should().ContainSingle().Which.Message.Should().Contain(provider);
    }

    [Fact]
    public async Task StreamAsync_ShouldYieldNothingAndCountDisabled_WhenTheStoredStateIsOff()
    {
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(
            new CountingFeatureStore(new AgentAssistFeatureState(false, "deepgram", Protect(Key), null, Admin)), factory);

        (await DrainAsync(NewWrapper(session))).Should().BeEmpty();

        factory.Calls.Should().BeEmpty("a session that reads the feature off sends no audio anywhere");
        _counts.Get(AgentAssistRecognizerMetrics.Disabled).Should().Be(1);
        Warnings().Should().ContainSingle();
    }

    [Fact]
    public async Task StreamAsync_ShouldLogOneWarningAndCountMissingValueOnce_WhenTheLanguageIsMissingAndBothStreamsStart()
    {
        var store = new CountingFeatureStore(Deepgram(Key, "wss://stt.internal.test"));
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(store, factory, language: null);

        await Task.WhenAll(DrainAsync(NewWrapper(session)), DrainAsync(NewWrapper(session)));

        factory.Calls.Should().BeEmpty();
        _counts.Get(AgentAssistRecognizerMetrics.MissingValue).Should().Be(1, "one session counts once, whatever its streams");
        _counts.Total.Should().Be(1);
        Warnings().Should().ContainSingle().Which.Message.Should().Contain(AgentAssistRecognizerSession.DeepgramLanguageSetting);
        _logs.RecordsContaining(Key).Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldCountMissingValueNamingTheKey_WhenNoApiKeyIsStored()
    {
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(
            new CountingFeatureStore(new AgentAssistFeatureState(true, "deepgram", null, null, Admin)), factory);

        await DrainAsync(NewWrapper(session));

        factory.Calls.Should().BeEmpty();
        _counts.Get(AgentAssistRecognizerMetrics.MissingValue).Should().Be(1);
        Warnings().Should().ContainSingle().Which.Message.Should().Contain(AgentAssistRecognizerSession.ApiKeyCredential);
    }

    [Fact]
    public async Task StreamAsync_ShouldCountMissingValueWithoutLoggingIt_WhenTheStoredEndpointIsNotAWebSocketUri()
    {
        const string Endpoint = "https://user:endpoint-secret-5512@stt.internal.test/v1/listen";
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(new CountingFeatureStore(Deepgram(Key, Endpoint)), factory);

        await DrainAsync(NewWrapper(session));

        factory.Calls.Should().BeEmpty();
        _counts.Get(AgentAssistRecognizerMetrics.MissingValue).Should().Be(1);
        Warnings().Should().ContainSingle().Which.Message.Should().Contain(AgentAssistRecognizerSession.EndpointCredential);
        _logs.RecordsContaining("endpoint-secret-5512").Should().BeEmpty();
        _logs.RecordsContaining(Key).Should().BeEmpty();
    }

    [Fact]
    public async Task StreamAsync_ShouldCountMissingValueWithoutTheException_WhenTheStoredCredentialsCannotBeDecrypted()
    {
        var foreign = new AgentAssistCredentialsProtector(new EphemeralDataProtectionProvider());
        var state = new AgentAssistFeatureState(
            true, "deepgram", foreign.Protect(new Dictionary<string, string> { ["apiKey"] = Key }), null, Admin);
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(new CountingFeatureStore(state), factory);

        await DrainAsync(NewWrapper(session));

        factory.Calls.Should().BeEmpty();
        _counts.Get(AgentAssistRecognizerMetrics.MissingValue).Should().Be(1);
        var warning = Warnings().Should().ContainSingle().Subject;
        warning.Exception.Should().BeNull("the exception text of a failed decryption is never logged");
        warning.Message.Should().Contain("decrypted");
    }

    // ---------------------------------------------------------------- one read per session

    [Fact]
    public async Task StreamAsync_ShouldReadTheToggleOnceAndUseOneKey_WhenBothStreamsOfASessionStartConcurrently()
    {
        var store = new CountingFeatureStore(Deepgram(Key)) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var factory = new RecordingRecognizerFactory();
        var session = NewSession(store, factory);

        var caller = Task.Run(() => DrainAsync(NewWrapper(session)));
        var agent = Task.Run(() => DrainAsync(NewWrapper(session)));
        await store.ReadStarted.Task.WaitAsync(Bound);
        // A rotation saved while the session's read is in flight: a second read would answer the new key.
        store.State = Deepgram(OtherKey);
        store.Hold.SetResult();
        await Task.WhenAll(caller, agent).WaitAsync(Bound);

        store.SessionReads.Should().Be(1, "read the toggle twice: both streams of one session wait on its one read");
        factory.Calls.Should().HaveCount(2);
        factory.Calls.Select(c => c.Settings.ApiKey).Distinct().Should().ContainSingle("one session never uses two keys");
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldReadAgain_WhenANewSessionStartsAfterARotation()
    {
        var store = new CountingFeatureStore(Deepgram(Key));
        var factory = new RecordingRecognizerFactory();

        await DrainAsync(NewWrapper(NewSession(store, factory)));
        store.State = Deepgram(OtherKey);
        await DrainAsync(NewWrapper(NewSession(store, factory)));

        store.SessionReads.Should().Be(2);
        factory.Calls.Select(c => c.Settings.ApiKey).Should().Equal(Key, OtherKey);
    }

    [Fact]
    public async Task GetSettingsAsync_ShouldLetTheOtherStreamFinishItsRead_WhenOneStreamGivesUpWhileTheReadIsInFlight()
    {
        var store = new CountingFeatureStore(Deepgram(Key)) { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var session = NewSession(store, new RecordingRecognizerFactory());
        using var giveUp = new CancellationTokenSource();

        var first = session.GetSettingsAsync(giveUp.Token);
        await store.ReadStarted.Task.WaitAsync(Bound);
        await giveUp.CancelAsync();
        var second = session.GetSettingsAsync(CancellationToken.None);
        store.Hold.SetResult();

        await FluentActions.Awaiting(() => first).Should().ThrowAsync<OperationCanceledException>();
        (await second.WaitAsync(Bound))!.ApiKey.Should().Be(Key);
        store.SessionReads.Should().Be(1);
    }

    // ---------------------------------------------------------------- disposal

    [Theory]
    [InlineData(RecordedStreamEnd.Complete)]
    [InlineData(RecordedStreamEnd.WaitForCancellation)]
    [InlineData(RecordedStreamEnd.Throw)]
    public async Task StreamAsync_ShouldDisposeTheInnerRecognizerWhenTheStreamEnds_WhenItCompletesIsCancelledOrThrows(
        RecordedStreamEnd end)
    {
        var factory = new RecordingRecognizerFactory(end);
        var wrapper = NewWrapper(NewSession(new CountingFeatureStore(Deepgram(Key)), factory));
        using var cancel = new CancellationTokenSource();

        var drain = DrainAsync(wrapper, cancel.Token);
        await factory.StreamsStarted(1).WaitAsync(Bound);
        if (end == RecordedStreamEnd.WaitForCancellation)
            await cancel.CancelAsync();
        var failure = await Record.ExceptionAsync(() => drain.WaitAsync(Bound));

        switch (end)
        {
            case RecordedStreamEnd.Complete:
                failure.Should().BeNull();
                break;
            case RecordedStreamEnd.WaitForCancellation:
                failure.Should().BeAssignableTo<OperationCanceledException>();
                break;
            default:
                failure.Should().BeOfType<InvalidOperationException>("a provider's failure reaches the caller");
                break;
        }

        factory.Built.Should().ContainSingle().Which.DisposeCalls.Should().Be(1, "the stream's end disposes its inner recognizer");
        await wrapper.DisposeAsync();
        factory.Built.Single().DisposeCalls.Should().Be(1, "the wrapper's disposal does not dispose it again");
    }

    [Fact]
    public async Task DisposeAsync_ShouldDisposeTheInnerRecognizerOnce_WhenCalledTwiceWhileAStreamIsRunning()
    {
        var factory = new RecordingRecognizerFactory(RecordedStreamEnd.WaitForCancellation);
        var wrapper = NewWrapper(NewSession(new CountingFeatureStore(Deepgram(Key)), factory));
        using var cancel = new CancellationTokenSource();
        var frames = new FrameSource(1);
        await using var stream = wrapper.StreamAsync(frames, AudioFormat.Slin16Mono8kHz, cancel.Token).GetAsyncEnumerator();
        var moved = stream.MoveNextAsync().AsTask();
        await factory.StreamsStarted(1).WaitAsync(Bound);

        await wrapper.DisposeAsync();
        await wrapper.DisposeAsync();
        await cancel.CancelAsync();
        await FluentActions.Awaiting(() => moved).Should().ThrowAsync<OperationCanceledException>();

        factory.Built.Single().DisposeCalls.Should().Be(1, "the iterator's finally finds the inner recognizer already released");
        _logs.Records.Count(r => r.Message.StartsWith("Released an Agent Assist stream recognizer", StringComparison.Ordinal))
            .Should().Be(1, "DisposeAsync is idempotent");
    }

    [Fact]
    public async Task DisposeAsync_ShouldBuildAndDisposeNothing_WhenTheStreamNeverStarted()
    {
        var factory = new RecordingRecognizerFactory();
        var store = new CountingFeatureStore(Deepgram(Key));
        var wrapper = NewWrapper(NewSession(store, factory));

        await wrapper.DisposeAsync();

        factory.Calls.Should().BeEmpty();
        store.SessionReads.Should().Be(0, "nothing is read until a stream starts");
        var act = async () => await DrainAsync(wrapper);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ---------------------------------------------------------------- helpers

    private AgentAssistRecognizerSession NewSession(
        IAgentAssistFeatureStore store, ISpeechRecognizerFactory factory, string? language = "es", string? model = null)
    {
        var options = new AgentAssistOptions
        {
            Deepgram = new AgentAssistDeepgramOptions { Language = language, Model = model },
        };
        return new AgentAssistRecognizerSession(
            store, _protector, Options.Create(options), factory, _metrics,
            _loggerFactory.CreateLogger<AgentAssistRecognizerSession>());
    }

    private ToggleBackedSpeechRecognizer NewWrapper(AgentAssistRecognizerSession session) =>
        new(session, _loggerFactory.CreateLogger<ToggleBackedSpeechRecognizer>());

    private IReadOnlyDictionary<string, string> Protect(string apiKey, string? endpoint = null)
    {
        var plain = new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = apiKey };
        if (endpoint is not null)
            plain["endpoint"] = endpoint;
        return _protector.Protect(plain);
    }

    private AgentAssistFeatureState Deepgram(string apiKey, string? endpoint = null) => Stored("deepgram", apiKey, endpoint);

    private AgentAssistFeatureState Stored(string provider, string apiKey, string? endpoint = null) =>
        new(true, provider, Protect(apiKey, endpoint), DateTimeOffset.UtcNow, Admin);

    private IReadOnlyList<CapturedLogRecord> Warnings() =>
        [.. _logs.Records.Where(r => r.Level == LogLevel.Warning)];

    private static Task<List<SpeechRecognitionResult>> DrainAsync(SpeechRecognizer recognizer, int frames = 2) =>
        DrainAsync(recognizer, new FrameSource(frames));

    private static Task<List<SpeechRecognitionResult>> DrainAsync(SpeechRecognizer recognizer, CancellationToken ct) =>
        DrainAsync(recognizer, new FrameSource(2), ct);

    private static async Task<List<SpeechRecognitionResult>> DrainAsync(
        SpeechRecognizer recognizer, FrameSource frames, CancellationToken ct = default)
    {
        var results = new List<SpeechRecognitionResult>();
        await foreach (var result in recognizer.StreamAsync(frames, AudioFormat.Slin16Mono8kHz, ct))
            results.Add(result);
        return results;
    }

    /// <summary>A stream of silent frames that counts how many were read.</summary>
    private sealed class FrameSource(int count) : IAsyncEnumerable<ReadOnlyMemory<byte>>
    {
        private int _read;

        public int Read => Volatile.Read(ref _read);

        public async IAsyncEnumerator<ReadOnlyMemory<byte>> GetAsyncEnumerator(CancellationToken ct = default)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                Interlocked.Increment(ref _read);
                yield return new byte[320];
            }
        }
    }
}
