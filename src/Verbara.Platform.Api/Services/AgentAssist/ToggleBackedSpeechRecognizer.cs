using System.Runtime.CompilerServices;
using Verbara.Sdk.Audio;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The recognizer of one Agent Assist stream, built from the stored feature state: on its first
/// <see cref="StreamAsync"/> it asks the session's <see cref="AgentAssistRecognizerSession"/> for the settings (one read
/// shared by both streams of the session) and transcribes through a recognizer the shared factory builds from them.
/// Registered transient through <c>WithSpeechRecognizer&lt;ToggleBackedSpeechRecognizer&gt;()</c>, so each stream gets
/// its own, resolved from the session's scope.
/// </summary>
/// <remarks>
/// <para>When the session cannot transcribe, the stream reads its audio to the end and yields nothing; no audio leaves
/// the process.</para>
/// <para>The inner recognizer is disposed when its stream ends, whether it completes, is cancelled or throws; the
/// wrapper's own <see cref="DisposeAsync"/> is idempotent and disposes an inner recognizer still held, and the session's
/// scope calls it at teardown.</para>
/// </remarks>
internal sealed partial class ToggleBackedSpeechRecognizer : SpeechRecognizer
{
    private readonly AgentAssistRecognizerSession _session;
    private readonly ILogger<ToggleBackedSpeechRecognizer> _logger;
    private SpeechRecognizer? _inner;
    private int _disposed;

    /// <summary>Builds the recognizer of one stream of the session that owns <paramref name="session"/>.</summary>
    /// <param name="session">The session's shared read of the feature state.</param>
    /// <param name="logger">Logs when the wrapper is released.</param>
    public ToggleBackedSpeechRecognizer(AgentAssistRecognizerSession session, ILogger<ToggleBackedSpeechRecognizer> logger)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(logger);
        _session = session;
        _logger = logger;
    }

    /// <inheritdoc />
    public override string ProviderName => "AgentAssistToggle";

    /// <inheritdoc />
    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(audioFrames);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var settings = await _session.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings is null)
        {
            await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
            {
                // The session transcribes nothing: each frame is read and dropped, never sent anywhere.
            }

            yield break;
        }

        var inner = _session.CreateRecognizer(settings);
        Volatile.Write(ref _inner, inner);
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Disposed while the inner recognizer was being built: release it here, since DisposeAsync missed it.
            await ReleaseAsync(inner).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(ToggleBackedSpeechRecognizer));
        }

        try
        {
            await foreach (var result in inner.StreamAsync(audioFrames, format, ct).ConfigureAwait(false))
                yield return result;
        }
        finally
        {
            await ReleaseAsync(inner).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var inner = Interlocked.Exchange(ref _inner, null);
        if (inner is not null)
            await inner.DisposeAsync().ConfigureAwait(false);

        LogReleased(_logger, inner is not null);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Disposes <paramref name="inner"/> unless <see cref="DisposeAsync"/> already took it.</summary>
    private async ValueTask ReleaseAsync(SpeechRecognizer inner)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _inner, null, inner), inner))
            await inner.DisposeAsync().ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Released an Agent Assist stream recognizer (inner recognizer still held: {HeldInner})")]
    private static partial void LogReleased(ILogger logger, bool heldInner);
}
