using Verbara.Platform.Core.Configuration;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Microsoft.Extensions.Options;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// Process-scoped in-memory <see cref="IAgentAssistFeatureToggle"/>. Seeds its state from
/// <see cref="AgentAssistOptions"/> (bound from appsettings) the first time the toggle is read,
/// then keeps the current state in a guarded field. Admin endpoint updates mutate this field
/// atomically; credential bytes are ALWAYS already encrypted by
/// <see cref="AgentAssistCredentialsProtector"/> before they land here.
/// </summary>
/// <remarks>
/// <para>
/// <b>One replica only:</b> the state lives in this process, so it is used only by a host without the
/// Platform database; with the database, <see cref="PostgresAgentAssistFeatureToggle"/> keeps one state
/// for every replica. Every read here is direct, so the engine, the administrator's page and a
/// session all see a change at once.
/// </para>
/// </remarks>
public sealed class InMemoryAgentAssistFeatureToggle : IAgentAssistFeatureStore
{
    private readonly AgentAssistOptions _seedOptions;
    private readonly AgentAssistCredentialsProtector _protector;
    private readonly Lock _lock = new();
    private AgentAssistFeatureState _current;
    private bool _seeded;

    public InMemoryAgentAssistFeatureToggle(
        IOptions<AgentAssistOptions> options,
        AgentAssistCredentialsProtector protector)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);
        _seedOptions = options.Value;
        _protector = protector;
        _current = AgentAssistFeatureState.Disabled; // replaced on first read via SeedIfNeeded()
    }

    public ValueTask<AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default)
    {
        SeedIfNeeded();
        lock (_lock)
        {
            return ValueTask.FromResult(_current);
        }
    }

    /// <inheritdoc />
    ValueTask<AgentAssistFeatureState> IAgentAssistFeatureStore.ReadAsync(CancellationToken ct) => GetCurrentAsync(ct);

    /// <inheritdoc />
    ValueTask<AgentAssistFeatureState> IAgentAssistFeatureStore.ReadForSessionAsync(CancellationToken ct) =>
        GetCurrentAsync(ct);

    public ValueTask<AgentAssistFeatureState> SetAsync(AgentAssistFeatureState desired, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        SeedIfNeeded();
        lock (_lock)
        {
            _current = desired;
            return ValueTask.FromResult(_current);
        }
    }

    private void SeedIfNeeded()
    {
        if (_seeded) return;
        lock (_lock)
        {
            if (_seeded) return;

            // Off unless configuration enables a provider (no audit tag on the off state, as before).
            var seed = AgentAssistFeatureSeed.FromOptions(_seedOptions, _protector, updatedAt: null);
            _current = seed.Enabled ? seed : AgentAssistFeatureState.Disabled;

            _seeded = true;
        }
    }
}
