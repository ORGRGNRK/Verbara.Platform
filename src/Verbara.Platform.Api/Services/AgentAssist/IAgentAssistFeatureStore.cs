using Verbara.Sdk.Pro.AgentAssist.Features;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// The Api's Agent Assist feature state, with the reads each caller needs. The engine's per-call check is
/// <see cref="IAgentAssistFeatureToggle.GetCurrentAsync"/>, which may be served from a short cache; the administrator's
/// page and a session's recognizer read the stored state directly, so a change applies from the next request on any
/// replica.
/// </summary>
internal interface IAgentAssistFeatureStore : IAgentAssistFeatureToggle
{
    /// <summary>
    /// The stored state, read directly (never from the engine's cache). The administrator's GET and PUT use it. A
    /// database error is thrown.
    /// </summary>
    ValueTask<AgentAssistFeatureState> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// The stored state for one Agent Assist session, read directly (never from the engine's cache), so a rotation or a
    /// switch to off applies to every session whose read starts after the change was saved. Only on a database error
    /// does it return the last state this replica knew (logged and counted), or the disabled state when it knew none.
    /// </summary>
    ValueTask<AgentAssistFeatureState> ReadForSessionAsync(CancellationToken ct = default);
}
