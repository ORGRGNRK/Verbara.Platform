namespace Verbara.Platform.Core.Configuration;

/// <summary>
/// Seed configuration for the AgentAssist runtime feature toggle. Bound from the
/// <c>"AgentAssist"</c> configuration section at startup; the resulting snapshot
/// is handed to <c>InMemoryAgentAssistFeatureToggle</c> which owns the live state.
/// Subsequent runtime changes via the admin endpoint update the in-memory toggle
/// only — they do NOT round-trip back to <c>appsettings.json</c>.
/// The same section carries host settings that are not part of the toggle: <see cref="AudioSocket"/> (where a snoop's
/// audio is sent) and the Deepgram recognizer's <see cref="AgentAssistDeepgramOptions.Model"/> and
/// <see cref="AgentAssistDeepgramOptions.Language"/>.
/// </summary>
public sealed class AgentAssistOptions
{
    /// <summary>Master on/off switch; when <c>false</c>, the engine skips transcription at session start.</summary>
    public bool Enabled { get; set; }

    /// <summary>Lowercase provider id: <c>"deepgram"</c> | <c>"whisper"</c> | <c>"azure-whisper"</c> | <c>"google"</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Where Asterisk sends a snoop's audio: <c>AgentAssist:AudioSocket:Port</c> and
    /// <c>AgentAssist:AudioSocket:AdvertisedHost</c>.
    /// </summary>
    public AgentAssistAudioSocketOptions AudioSocket { get; set; } = new();

    /// <summary>
    /// Deepgram WebSocket streaming: the seed credentials (<c>ApiKey</c>, <c>Endpoint</c>) and the recognizer's
    /// <c>Model</c> and <c>Language</c>.
    /// </summary>
    public AgentAssistDeepgramOptions Deepgram { get; set; } = new();

    /// <summary>OpenAI Whisper REST credentials.</summary>
    public AgentAssistProviderCredentials Whisper { get; set; } = new();

    /// <summary>Azure Cognitive Services Whisper credentials (Endpoint + ApiKey).</summary>
    public AgentAssistProviderCredentials Azure { get; set; } = new();

    /// <summary>Google Cloud Speech-to-Text credentials (ApiKey holds service-account JSON or API key).</summary>
    public AgentAssistProviderCredentials Google { get; set; } = new();
}

/// <summary>
/// The AudioSocket address a snoop's audio is sent to, bound from <c>AgentAssist:AudioSocket</c>
/// (environment <c>AgentAssist__AudioSocket__Port</c>, <c>AgentAssist__AudioSocket__AdvertisedHost</c>). The host
/// passes <see cref="Port"/> to the engine's AudioSocket listener and <c>AdvertisedHost:Port</c> as the address set on
/// each snoop. A missing or unresolvable host never fails startup: each call is then skipped and counted.
/// </summary>
public sealed class AgentAssistAudioSocketOptions
{
    /// <summary>The <see cref="AdvertisedHost"/> value that asks for this replica's own address, resolved at first use.</summary>
    public const string AutoAdvertisedHost = "auto";

    /// <summary>The TCP port the AudioSocket listener binds and the advertised address names. Default 9093.</summary>
    public int Port { get; set; } = 9093;

    /// <summary>
    /// The host Asterisk connects to for this replica's snoops: a literal host name or IP address, or
    /// <see cref="AutoAdvertisedHost"/> for the local address of the route to the ARI host, resolved per replica at
    /// first use. It must name this replica only, never a service name that resolves to every replica. <c>null</c>
    /// (the default): no address, so no snoop is created.
    /// </summary>
    public string? AdvertisedHost { get; set; }
}

/// <summary>
/// Deepgram settings, bound from <c>AgentAssist:Deepgram</c>. <see cref="ApiKey"/> and <see cref="Endpoint"/> only
/// seed the feature toggle's encrypted credentials; <see cref="Model"/> and <see cref="Language"/> are read by the
/// recognizer each session builds.
/// </summary>
public sealed class AgentAssistDeepgramOptions
{
    /// <summary>API key, used only to seed the feature toggle.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional <c>ws://</c> or <c>wss://</c> endpoint, used only to seed the feature toggle.</summary>
    public string? Endpoint { get; set; }

    /// <summary><c>AgentAssist:Deepgram:Model</c>: the Deepgram model; optional, the recognizer's default when unset.</summary>
    public string? Model { get; set; }

    /// <summary>
    /// <c>AgentAssist:Deepgram:Language</c>: the language the calls are transcribed in. Required for a session to
    /// transcribe; without it the session gets no transcript and reports the value as missing.
    /// </summary>
    public string? Language { get; set; }
}

/// <summary>Per-provider credential material. Plain strings here are NEVER persisted; they are
/// always encrypted via <c>IDataProtectionProvider</c> before being handed to the feature toggle.</summary>
public sealed class AgentAssistProviderCredentials
{
    /// <summary>API key / secret. For Google this field holds the service-account JSON text.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Optional provider-specific endpoint (required for Azure, optional for Whisper compatible servers).</summary>
    public string? Endpoint { get; set; }
}
