using Microsoft.Extensions.DependencyInjection.Extensions;
using Verbara.Platform.Api.Services.AgentAssist;

namespace Verbara.Platform.Api.DependencyInjection;

/// <summary>
/// The services a <see cref="ToggleBackedSpeechRecognizer"/> needs: the shared recognizer factory, the named client
/// <c>agentassist-stt</c>, the scoped <see cref="AgentAssistRecognizerSession"/> and its counter. The host registers the
/// recognizer itself on the Agent Assist builder with <c>WithSpeechRecognizer&lt;ToggleBackedSpeechRecognizer&gt;()</c>.
/// </summary>
internal static class AgentAssistRecognizerExtensions
{
    /// <summary>Registers the services of the Agent Assist recognizer.</summary>
    /// <param name="services">The host's services; they must already provide <see cref="IAgentAssistFeatureStore"/>,
    /// <see cref="AgentAssistCredentialsProtector"/>, the <c>AgentAssist</c> options and logging.</param>
    public static IServiceCollection AddAgentAssistRecognizer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSpeechRecognizerFactory();
        services.AddHttpClient(AgentAssistRecognizerSession.HttpClientName);
        services.TryAddSingleton<AgentAssistRecognizerMetrics>();
        // Scoped: one per engine session scope, shared by the session's two recognizers (Pro's per-session scope).
        services.TryAddScoped<AgentAssistRecognizerSession>();
        return services;
    }
}
