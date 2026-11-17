using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// What one Agent Assist session transcribes with, shared by both of its recognizers: registered as a scoped service,
/// so the two <see cref="ToggleBackedSpeechRecognizer"/>s the engine resolves from one session's scope inject the same
/// instance, and no other session does.
/// </summary>
/// <remarks>
/// <para>
/// <b>One read per session.</b> The first stream to start reads the stored state through
/// <see cref="IAgentAssistFeatureStore.ReadForSessionAsync"/> (directly, never the engine's cache), decrypts the
/// credentials once and decides once whether the session can transcribe. The other stream waits on that same read, so
/// one session never uses two keys, and a key rotated or a feature switched off applies from the next session whose
/// read starts after the change was saved.
/// </para>
/// <para>
/// <b>One report per session.</b> When the session cannot transcribe it logs one Warning that names what is wrong
/// (never a value) and counts <c>platform_agentassist_recognizer_unavailable_total{reason}</c> once.
/// </para>
/// </remarks>
internal sealed partial class AgentAssistRecognizerSession
{
    /// <summary>The named <see cref="HttpClient"/> the session's recognizers are built with.</summary>
    public const string HttpClientName = "agentassist-stt";

    /// <summary>The stored credential that holds the provider's API key.</summary>
    internal const string ApiKeyCredential = "apiKey";

    /// <summary>The stored credential that holds the provider's endpoint, when one is set.</summary>
    internal const string EndpointCredential = "endpoint";

    /// <summary>The setting a Deepgram session needs to transcribe.</summary>
    internal const string DeepgramLanguageSetting = "AgentAssist:Deepgram:Language";

    private readonly IAgentAssistFeatureStore _store;
    private readonly AgentAssistCredentialsProtector _protector;
    private readonly AgentAssistOptions _options;
    private readonly ISpeechRecognizerFactory _factory;
    private readonly AgentAssistRecognizerMetrics _metrics;
    private readonly ILogger<AgentAssistRecognizerSession> _logger;
    private readonly Lazy<Task<SpeechRecognizerSettings?>> _settings;

    /// <summary>Builds the session object; nothing is read until a stream starts.</summary>
    /// <param name="store">The stored feature state.</param>
    /// <param name="protector">Decrypts the stored credentials.</param>
    /// <param name="options">The <c>AgentAssist</c> section: the provider's language and model.</param>
    /// <param name="factory">The shared recognizer factory.</param>
    /// <param name="metrics">The unavailable-session counter.</param>
    /// <param name="logger">Logs a session that cannot transcribe.</param>
    public AgentAssistRecognizerSession(
        IAgentAssistFeatureStore store,
        AgentAssistCredentialsProtector protector,
        IOptions<AgentAssistOptions> options,
        ISpeechRecognizerFactory factory,
        AgentAssistRecognizerMetrics metrics,
        ILogger<AgentAssistRecognizerSession> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _protector = protector;
        _options = options.Value;
        _factory = factory;
        _metrics = metrics;
        _logger = logger;
        _settings = new Lazy<Task<SpeechRecognizerSettings?>>(ResolveAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// The settings this session transcribes with, read once for the session; <see langword="null"/> when the session
    /// transcribes nothing (already logged and counted).
    /// </summary>
    /// <param name="ct">Stops this caller's wait only; the session's one read keeps going for the other stream.</param>
    public Task<SpeechRecognizerSettings?> GetSettingsAsync(CancellationToken ct) => _settings.Value.WaitAsync(ct);

    /// <summary>Builds one recognizer for <paramref name="settings"/> through the shared factory.</summary>
    /// <returns>A new recognizer; the caller owns it and disposes it.</returns>
    public SpeechRecognizer CreateRecognizer(SpeechRecognizerSettings settings) =>
        _factory.Create(settings, HttpClientName);

    private async Task<SpeechRecognizerSettings?> ResolveAsync()
    {
        // No caller's token: the read is the session's, and one stream giving up must not fail it for the other.
        var state = await _store.ReadForSessionAsync(CancellationToken.None).ConfigureAwait(false);

        if (!state.Enabled)
            return Unavailable(AgentAssistRecognizerMetrics.Disabled, "the feature is off");

        var provider = string.IsNullOrWhiteSpace(state.Provider) ? null : state.Provider.Trim().ToLowerInvariant();
        if (provider is null
            || !SpeechRecognizerFactory.TryGetMode(provider, out var mode)
            || mode != SpeechRecognizerMode.Streaming)
        {
            return Unavailable(
                AgentAssistRecognizerMetrics.ProviderNotLive,
                provider is null
                    ? "no provider is stored"
                    : $"provider '{provider}' cannot transcribe a live call");
        }

        IReadOnlyDictionary<string, string> credentials;
        try
        {
            credentials = state.ProtectedCredentials is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : _protector.Unprotect(state.ProtectedCredentials);
        }
        catch (CryptographicException)
        {
            // Never the exception itself: only that the stored credentials could not be read with this keyring.
            return Unavailable(AgentAssistRecognizerMetrics.MissingValue, "the stored credentials cannot be decrypted");
        }

        var missing = new List<string>(3);
        var apiKey = credentials.GetValueOrDefault(ApiKeyCredential);
        if (string.IsNullOrWhiteSpace(apiKey))
            missing.Add(ApiKeyCredential);

        Uri? endpoint = null;
        var endpointText = credentials.GetValueOrDefault(EndpointCredential);
        if (!string.IsNullOrWhiteSpace(endpointText)
            && (!Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint)
                || (endpoint.Scheme != Uri.UriSchemeWs && endpoint.Scheme != Uri.UriSchemeWss)))
        {
            missing.Add(EndpointCredential + " (not an absolute ws:// or wss:// URI)");
        }

        var language = _options.Deepgram.Language;
        if (string.IsNullOrWhiteSpace(language))
            missing.Add(DeepgramLanguageSetting);

        if (missing.Count > 0)
            return Unavailable(AgentAssistRecognizerMetrics.MissingValue, "missing value " + string.Join(", ", missing));

        return new SpeechRecognizerSettings
        {
            ProviderId = provider,
            Language = language!.Trim(),
            ApiKey = apiKey,
            Endpoint = endpoint,
            Model = string.IsNullOrWhiteSpace(_options.Deepgram.Model) ? null : _options.Deepgram.Model.Trim(),
        };
    }

    private SpeechRecognizerSettings? Unavailable(string reason, string detail)
    {
        LogUnavailable(_logger, reason, detail);
        _metrics.RecordUnavailable(reason);
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent Assist session transcribes nothing ({Reason}): {Detail}")]
    private static partial void LogUnavailable(ILogger logger, string reason, string detail);
}
