using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services.Speech;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The shared <see cref="SpeechRecognizerFactory"/>: one singleton that every caller building an Sdk
/// speech recognizer goes through (post-call analysis, Agent Assist).
/// </summary>
internal static class SpeechRecognizerFactoryExtensions
{
    /// <summary>
    /// Registers <see cref="SpeechRecognizerFactory"/> as a singleton, unless one is registered, and
    /// <see cref="ISpeechRecognizerFactory"/> as that same instance.
    /// </summary>
    /// <param name="services">The host's services; they must already provide <c>IHttpClientFactory</c> and logging.</param>
    public static IServiceCollection AddSpeechRecognizerFactory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<SpeechRecognizerFactory>();
        services.TryAddSingleton<ISpeechRecognizerFactory>(sp => sp.GetRequiredService<SpeechRecognizerFactory>());
        return services;
    }
}
