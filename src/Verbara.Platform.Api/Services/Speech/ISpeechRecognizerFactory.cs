using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Services.Speech;

/// <summary>
/// Builds an Sdk <see cref="SpeechRecognizer"/> from a provider id and its options. Implemented by
/// <see cref="SpeechRecognizerFactory"/> alone; callers depend on this interface so a test can stand a recording
/// recognizer in for a provider's real one.
/// </summary>
internal interface ISpeechRecognizerFactory
{
    /// <summary>Builds a recognizer for <paramref name="settings"/>; see <see cref="SpeechRecognizerFactory.Create"/>.</summary>
    /// <param name="settings">The provider id and its options.</param>
    /// <param name="httpClientName">The named <see cref="HttpClient"/> for a provider that sends HTTP requests.</param>
    /// <returns>A new recognizer; the caller owns it and disposes it.</returns>
    SpeechRecognizer Create(SpeechRecognizerSettings settings, string? httpClientName = null);
}
