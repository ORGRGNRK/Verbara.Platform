using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The feature state the <c>AgentAssist</c> configuration section describes, used to seed the toggle until an
/// administrator writes it. Credentials are encrypted before they enter the state.
/// </summary>
internal static class AgentAssistFeatureSeed
{
    /// <summary>The <see cref="AgentAssistFeatureState.UpdatedBy"/> of a state that came from configuration.</summary>
    public const string UpdatedBy = "appsettings";

    /// <summary>
    /// The configured state: off (with no provider and no credentials) unless <c>Enabled</c> is true and a provider is
    /// named; otherwise that provider, lowercased, with its configured <c>apiKey</c> and <c>endpoint</c> encrypted.
    /// </summary>
    public static AgentAssistFeatureState FromOptions(
        AgentAssistOptions options, AgentAssistCredentialsProtector protector, DateTimeOffset? updatedAt)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);

        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Provider))
        {
            return new AgentAssistFeatureState(
                Enabled: false, Provider: null, ProtectedCredentials: null, UpdatedAt: updatedAt, UpdatedBy: UpdatedBy);
        }

        var provider = options.Provider.Trim().ToLowerInvariant();
        var plain = ExtractCredentials(options, provider);
        return new AgentAssistFeatureState(
            Enabled: true,
            Provider: provider,
            ProtectedCredentials: plain.Count > 0 ? protector.Protect(plain) : null,
            UpdatedAt: updatedAt,
            UpdatedBy: UpdatedBy);
    }

    private static Dictionary<string, string> ExtractCredentials(AgentAssistOptions options, string provider)
    {
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        (string? ApiKey, string? Endpoint)? source = provider switch
        {
            "deepgram" => (options.Deepgram.ApiKey, options.Deepgram.Endpoint),
            "whisper" => (options.Whisper.ApiKey, options.Whisper.Endpoint),
            "azure-whisper" => (options.Azure.ApiKey, options.Azure.Endpoint),
            "google" => (options.Google.ApiKey, options.Google.Endpoint),
            _ => null,
        };
        if (source is not { } seed)
            return credentials;

        if (!string.IsNullOrWhiteSpace(seed.ApiKey))
            credentials["apiKey"] = seed.ApiKey;
        if (!string.IsNullOrWhiteSpace(seed.Endpoint))
            credentials["endpoint"] = seed.Endpoint;
        return credentials;
    }
}
