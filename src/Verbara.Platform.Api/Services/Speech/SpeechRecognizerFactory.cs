using System.Collections.Frozen;
using Microsoft.Extensions.Options;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.Stt.Deepgram;
using Verbara.Sdk.VoiceAi.Stt.Whisper;

namespace Verbara.Platform.Api.Services.Speech;

/// <summary>How a provider's Sdk recognizer returns text.</summary>
internal enum SpeechRecognizerMode
{
    /// <summary>Text only after the input audio ends (one request per input).</summary>
    Batch,

    /// <summary>Text while the input audio is still arriving.</summary>
    Streaming,
}

/// <summary>
/// The one place Platform builds an Sdk <see cref="SpeechRecognizer"/>: from a provider id, that
/// provider's options (<see cref="SpeechRecognizerSettings"/>) and, for a provider that sends HTTP
/// requests, an <see cref="HttpClient"/> the caller names. Every caller that needs a recognizer goes
/// through it and adds a provider by adding a mapping here, never a factory of its own.
/// </summary>
/// <remarks>
/// <para>Mapped today: <c>whisper</c> (batch; any Whisper-compatible endpoint) and <c>deepgram</c> (streaming, over a
/// WebSocket; an endpoint given without a route gets Deepgram's <c>/v1/listen</c>). An unmapped id is refused
/// with an <see cref="ArgumentException"/> naming it. Which of the mapped providers a caller accepts is
/// that caller's rule, checked at its own startup or admin validation; <see cref="TryGetMode"/> lets it
/// tell batch providers from streaming ones from the same table the factory builds from.</para>
/// <para>Static dispatch only (a frozen table of delegates), so it holds under Native AOT. Nothing it
/// logs or throws carries an API key or the user information or query of an endpoint: only the provider
/// id, the language, the endpoint host and the model.</para>
/// </remarks>
internal sealed partial class SpeechRecognizerFactory(
    IHttpClientFactory httpClients,
    ILogger<SpeechRecognizerFactory> logger) : ISpeechRecognizerFactory
{
    private static readonly FrozenDictionary<string, ProviderMapping> s_providers =
        new Dictionary<string, ProviderMapping>(StringComparer.Ordinal)
        {
            ["whisper"] = new(SpeechRecognizerMode.Batch, NeedsHttpClient: true, BuildWhisper),
            ["deepgram"] = new(SpeechRecognizerMode.Streaming, NeedsHttpClient: false, BuildDeepgram),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Deepgram's live transcription route, used when an endpoint names a host only.</summary>
    internal const string DeepgramListenRoute = "/v1/listen";

    /// <summary>The provider ids this factory can build.</summary>
    public static IReadOnlyCollection<string> MappedProviders => s_providers.Keys;

    /// <summary>Whether <paramref name="providerId"/> is mapped, and how its recognizer returns text.</summary>
    /// <param name="providerId">Lowercase provider id; the match is exact.</param>
    /// <param name="mode">The provider's mode when it is mapped.</param>
    public static bool TryGetMode(string providerId, out SpeechRecognizerMode mode)
    {
        ArgumentNullException.ThrowIfNull(providerId);

        if (s_providers.TryGetValue(providerId, out var mapping))
        {
            mode = mapping.Mode;
            return true;
        }

        mode = default;
        return false;
    }

    /// <summary>Builds a recognizer for <paramref name="settings"/>.</summary>
    /// <param name="settings">The provider id and its options.</param>
    /// <param name="httpClientName">The named <see cref="HttpClient"/> to send the provider's requests
    /// with; required by a provider that sends HTTP requests, ignored by one that does not.</param>
    /// <returns>A new recognizer; the caller owns it and disposes it.</returns>
    /// <exception cref="ArgumentException">The provider id is not mapped, the language is blank, the
    /// provider needs a named <see cref="HttpClient"/> and none was given, or the endpoint has a scheme the
    /// provider cannot open.</exception>
    public SpeechRecognizer Create(SpeechRecognizerSettings settings, string? httpClientName = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!s_providers.TryGetValue(settings.ProviderId, out var mapping))
        {
            LogRefusedUnmapped(logger, settings.ProviderId);
            throw new ArgumentException(
                $"Speech provider '{settings.ProviderId}' has no recognizer mapping; mapped providers: " +
                $"{string.Join(", ", s_providers.Keys)}.",
                nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            throw new ArgumentException(
                $"Speech provider '{settings.ProviderId}' needs a language; none was given.", nameof(settings));
        }

        HttpClient? http = null;
        if (mapping.NeedsHttpClient)
        {
            if (string.IsNullOrWhiteSpace(httpClientName))
            {
                throw new ArgumentException(
                    $"Speech provider '{settings.ProviderId}' sends HTTP requests and needs a named HttpClient; none was given.",
                    nameof(httpClientName));
            }

            http = httpClients.CreateClient(httpClientName);
        }

        var recognizer = mapping.Build(settings, http);
        var endpointHost = SpeechRecognizerSettings.SafeEndpointHost(settings.Endpoint);
        LogBuilt(logger, settings.ProviderId, settings.Language, endpointHost, settings.Model ?? "(default)");
        return recognizer;
    }

    private static WhisperSpeechRecognizer BuildWhisper(SpeechRecognizerSettings settings, HttpClient? http)
    {
        var options = new WhisperOptions
        {
            ApiKey = settings.ApiKey ?? string.Empty,
            Language = settings.Language,
        };
        if (settings.Endpoint is not null)
            options.Endpoint = settings.Endpoint;
        if (!string.IsNullOrWhiteSpace(settings.Model))
            options.Model = settings.Model;

        return new WhisperSpeechRecognizer(Options.Create(options), http!);
    }

    private static DeepgramSpeechRecognizer BuildDeepgram(SpeechRecognizerSettings settings, HttpClient? http) =>
        new(Options.Create(DeepgramOptionsFor(settings)));

    /// <summary>
    /// The Deepgram options <paramref name="settings"/> map to: the key, the language, the model when set (the Sdk's
    /// default otherwise) and the endpoint as the WebSocket base URI when set. An endpoint without a route
    /// (<c>wss://api.deepgram.com</c>) gets Deepgram's <c>/v1/listen</c>; its query and fragment are dropped, because the
    /// recognizer appends its own query.
    /// </summary>
    /// <exception cref="ArgumentException">The endpoint is not an absolute <c>ws://</c> or <c>wss://</c> URI; the message
    /// names the provider only, never the endpoint.</exception>
    internal static DeepgramOptions DeepgramOptionsFor(SpeechRecognizerSettings settings)
    {
        var options = new DeepgramOptions
        {
            ApiKey = settings.ApiKey ?? string.Empty,
            Language = settings.Language,
        };
        if (!string.IsNullOrWhiteSpace(settings.Model))
            options.Model = settings.Model;

        if (settings.Endpoint is { } endpoint)
        {
            if (!endpoint.IsAbsoluteUri
                || (endpoint.Scheme != Uri.UriSchemeWs && endpoint.Scheme != Uri.UriSchemeWss))
            {
                throw new ArgumentException(
                    $"Speech provider '{settings.ProviderId}' needs an absolute ws:// or wss:// endpoint.",
                    nameof(settings));
            }

            options.BaseUri = endpoint.AbsolutePath == "/"
                ? endpoint.GetLeftPart(UriPartial.Authority) + DeepgramListenRoute
                : endpoint.GetLeftPart(UriPartial.Path);
        }

        return options;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Built the {ProviderId} speech recognizer for language {Language}, endpoint host {EndpointHost}, model {Model}")]
    private static partial void LogBuilt(
        ILogger logger, string providerId, string language, string endpointHost, string model);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refused to build a speech recognizer for unmapped provider {ProviderId}")]
    private static partial void LogRefusedUnmapped(ILogger logger, string providerId);

    private sealed record ProviderMapping(
        SpeechRecognizerMode Mode,
        bool NeedsHttpClient,
        Func<SpeechRecognizerSettings, HttpClient?, SpeechRecognizer> Build);
}
