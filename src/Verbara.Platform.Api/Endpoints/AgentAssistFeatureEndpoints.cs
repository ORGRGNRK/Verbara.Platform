using System.Diagnostics.CodeAnalysis;
using Verbara.Platform.Api.Auth;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Audit;
using Verbara.Platform.Core;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.MultiTenant;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Verbara.Platform.Api.Endpoints;

/// <summary>
/// Runtime feature-management surface for AgentAssist. Lets a platform-admin toggle the
/// engine on/off and rotate STT provider credentials without restarting the API host.
/// Credentials are never returned to the client (redacted to <c>"***"</c>) and never
/// captured in audit rows — only the enabled flag + provider name land in the audit trail.
/// </summary>
/// <remarks>
/// The toggle is one for the whole deployment, so only the Platform tenant reaches it: a Platform
/// tenant admin, or a user of that tenant holding <c>features:agent-assist:manage</c>, or a
/// management key scoped to it. A Customer or Partner tenant's caller — an impersonation session
/// included — is refused with 403, whatever permissions its role holds.
/// </remarks>
internal static class AgentAssistFeatureEndpoints
{
    /// <summary>
    /// Providers whose recognizer streams text while the call is still in progress. Only these can
    /// be enabled: Agent Assist transcribes live calls.
    /// </summary>
    private static readonly HashSet<string> LiveProviders = new(StringComparer.Ordinal)
    {
        "deepgram",
    };

    /// <summary>
    /// Providers whose recognizer returns text only after the audio stream ends — on a live call,
    /// at hangup. They are refused with a message naming the reason.
    /// </summary>
    private static readonly HashSet<string> BatchProviders = new(StringComparer.Ordinal)
    {
        "whisper", "azure-whisper", "google",
    };

    private const string LiveProviderList = "deepgram";

    /// <summary>The Platform-tenant gate with <c>features:agent-assist:manage</c>.</summary>
    internal static readonly AuthorizationPolicy ManagePolicy = new AuthorizationPolicyBuilder()
        .AddRequirements(new PlatformAdminRequirement(PlatformAdminPermissions.AgentAssistFeatureManage))
        .Build();

    public static void MapAgentAssistFeatureEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/features/agent-assist")
            .RequireAuthorization(ManagePolicy);

        group.MapGet("", GetFeature);
        group.MapPut("", UpdateFeature);
    }

    // GET and PUT read the stored state directly, never the engine's cache, so a GET on any
    // replica right after a PUT on another answers the new state.
    private static async Task<Ok<AgentAssistFeatureDto>> GetFeature(
        [FromServices] IAgentAssistFeatureStore toggle,
        CancellationToken ct)
    {
        var state = await toggle.ReadAsync(ct);
        return TypedResults.Ok(ToDto(state));
    }

    private static async Task<Results<Ok<AgentAssistFeatureDto>, ValidationProblem>> UpdateFeature(
        HttpContext context,
        [FromBody] AgentAssistFeatureUpdateRequest body,
        [FromServices] IAgentAssistFeatureStore toggle,
        [FromServices] AgentAssistCredentialsProtector protector,
        [FromServices] ITenantStore tenantStore,
        [FromServices] IAuditService audit,
        [FromServices] IClock clock,
        CancellationToken ct)
    {
        // Validate provider when enabling. Normalize first, then check — ensures
        // the contract ("state.Provider is always lowercase") matches the check surface.
        string? normalizedProvider = null;
        if (body.Enabled)
        {
            var candidate = body.Provider?.Trim().ToLowerInvariant();
            if (!IsLiveProvider(candidate, out var providerError))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["provider"] = [providerError],
                });
            }
            normalizedProvider = candidate;

            // Provider-specific credential requirements
            var creds = body.Credentials ?? new AgentAssistCredentialsDto(null, null);
            var missing = ValidateCredentials(normalizedProvider, creds);
            if (missing is not null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["credentials"] = [missing],
                });
            }
        }

        // A present endpoint must be a WebSocket URI, whatever `enabled` says: the live
        // recognizer connects to it as a WebSocket.
        var endpoint = body.Credentials?.Endpoint?.Trim();
        if (!string.IsNullOrEmpty(endpoint) && !IsWebSocketUri(endpoint))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["credentials"] = ["endpoint must be an absolute ws:// or wss:// URI."],
            });
        }

        var previous = await toggle.ReadAsync(ct);

        // Encrypt credentials before they enter the toggle state
        IReadOnlyDictionary<string, string>? encrypted = null;
        if (body.Enabled && body.Credentials is not null)
        {
            var plain = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(body.Credentials.ApiKey))
                plain["apiKey"] = body.Credentials.ApiKey!;
            if (!string.IsNullOrEmpty(endpoint))
                plain["endpoint"] = endpoint;
            if (plain.Count > 0)
                encrypted = protector.Protect(plain);
        }

        var actor = context.User.FindFirst("sub")?.Value
                    ?? context.User.FindFirst("user_id")?.Value
                    ?? "system";

        var next = new AgentAssistFeatureState(
            Enabled: body.Enabled,
            Provider: normalizedProvider,
            ProtectedCredentials: encrypted,
            UpdatedAt: clock.UtcNow,
            UpdatedBy: actor);

        var persisted = await toggle.SetAsync(next, ct);

        // Audit — credentials NEVER included, only metadata
        var host = await tenantStore.GetHostTenantAsync(ct);
        var auditTenant = host is not null ? new TenantId(host.TenantId) : new TenantId("platform");
        await audit.RecordAsync(
            auditTenant,
            category: "admin",
            action: "agentassist.config.changed",
            severity: "warning",
            actorId: actor,
            actorType: "user",
            targetId: "agent-assist",
            targetType: "feature",
            changes: new AuditChanges(
                Before: new { previous.Enabled, previous.Provider },
                After: new { persisted.Enabled, persisted.Provider }),
            metadata: new Dictionary<string, string>
            {
                ["ip"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                ["endpoint"] = context.Request.Path.Value ?? "",
            },
            ct: ct);

        return TypedResults.Ok(ToDto(persisted));
    }

    private static AgentAssistFeatureDto ToDto(AgentAssistFeatureState state)
    {
        // Surfaces whether credentials are present (so the UI can show "configured" state)
        // without leaking any part of the ciphertext.
        var hasCreds = state.ProtectedCredentials is { Count: > 0 };
        return new AgentAssistFeatureDto(
            Enabled: state.Enabled,
            Provider: state.Provider,
            CredentialsConfigured: hasCreds,
            ApiKeyMasked: hasCreds ? "***" : null,
            EndpointMasked: hasCreds && state.ProtectedCredentials!.ContainsKey("endpoint") ? "***" : null,
            UpdatedAt: state.UpdatedAt,
            UpdatedBy: state.UpdatedBy);
    }

    private static bool IsLiveProvider(
        [NotNullWhen(true)] string? provider,
        [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            error = $"Provider is required when enabling. Live providers: {LiveProviderList}.";
            return false;
        }
        if (LiveProviders.Contains(provider))
        {
            error = null;
            return true;
        }
        error = BatchProviders.Contains(provider)
            ? $"Provider '{provider}' cannot transcribe a live call: its recognizer returns text "
              + $"only after the audio ends. Live providers: {LiveProviderList}."
            : $"Provider must be one of: {LiveProviderList}.";
        return false;
    }

    private static string? ValidateCredentials(string provider, AgentAssistCredentialsDto creds)
    {
        return provider switch
        {
            "deepgram" => string.IsNullOrWhiteSpace(creds.ApiKey) ? "Deepgram requires apiKey." : null,
            _ => "Unknown provider.",
        };
    }

    private static bool IsWebSocketUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeWs || uri.Scheme == Uri.UriSchemeWss)
        && !string.IsNullOrEmpty(uri.Host);
}

// ─── DTOs ─────────────────────────────────────────────────────────────────────

internal sealed record AgentAssistFeatureDto(
    bool Enabled,
    string? Provider,
    bool CredentialsConfigured,
    string? ApiKeyMasked,
    string? EndpointMasked,
    DateTimeOffset? UpdatedAt,
    string? UpdatedBy);

internal sealed record AgentAssistFeatureUpdateRequest(
    bool Enabled,
    string? Provider,
    AgentAssistCredentialsDto? Credentials);

internal sealed record AgentAssistCredentialsDto(
    string? ApiKey,
    string? Endpoint);
