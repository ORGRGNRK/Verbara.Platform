using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Tests.AgentAssist;
using Verbara.Sdk.Pro.AgentAssist.Features;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Verbara.Platform.Api.Tests;

/// <summary>
/// HTTP contract tests for the AgentAssist runtime feature endpoint surface
/// (<c>GET/PUT /api/v1/admin/features/agent-assist</c>) introduced in R5.1 Task J.
/// Covers happy paths, auth gates, and validation rules — credentials must never
/// leak through GET, PUT without credentials should be rejected, etc. The caller of the
/// happy paths is the deployment operator (a Platform tenant admin); the Customer-tenant
/// refusals are in <see cref="AgentAssistAdminScopeTests"/>.
/// </summary>
public sealed class AgentAssistFeatureEndpointsTests : IClassFixture<AgentAssistTenantScopeApiFactory>
{
    private readonly AgentAssistTenantScopeApiFactory _factory;

    public AgentAssistFeatureEndpointsTests(AgentAssistTenantScopeApiFactory factory) => _factory = factory;

    // ─── GET ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFeature_ShouldReturn200_WithRedactedCredentials_WhenPlatformOperatorAuthenticated()
    {
        using var client = _factory.PlatformOperatorClient();

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeFalse(
            because: "fresh test host seeds the toggle from empty options → disabled");
        // apiKeyMasked must be null OR "***", never the raw secret
        var masked = json["apiKeyMasked"];
        (masked is null || masked.GetValue<string>() == "***").Should().BeTrue();
    }

    [Fact]
    public async Task GetFeature_ShouldAnswerTheStoredState_NotTheEngineCache()
    {
        var store = new SplitFeatureStore();
        await using var factory = new AgentAssistTenantScopeApiFactory { FeatureStore = store };
        using var client = factory.PlatformOperatorClient();

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeTrue(
            because: "the administrator's GET reads the stored state directly, never the engine's cached answer");
        json["updatedBy"]!.GetValue<string>().Should().Be("stored");
        store.EngineReads.Should().Be(0);
    }

    [Fact]
    public async Task GetFeature_ShouldReturn401Or403_WhenNotAuthenticated()
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.CreateClient(); // no Authorization header

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetFeature_ShouldReturn403_WhenAgentRoleLacksPermission()
    {
        await using var factory = new NonAdminAuthenticatedApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/v1/admin/features/agent-assist");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            because: "Agent-role users must not reach the runtime feature surface");
    }

    // ─── PUT ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateFeature_ShouldReturn200_AndPersist_WhenValidDeepgramPayload()
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();

        var body = new
        {
            enabled = true,
            provider = "deepgram",
            credentials = new { apiKey = "dg_live_1234567890" },
        };

        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeTrue();
        json["provider"]!.GetValue<string>().Should().Be("deepgram");
        json["credentialsConfigured"]!.GetValue<bool>().Should().BeTrue();
        json["apiKeyMasked"]!.GetValue<string>().Should().Be("***");

        // Round-trip: the toggle in DI must reflect the new state
        var toggle = factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var state = await toggle.GetCurrentAsync();
        state.Enabled.Should().BeTrue();
        state.Provider.Should().Be("deepgram");
        state.ProtectedCredentials.Should().NotBeNull();
        state.ProtectedCredentials!["apiKey"].Should().NotBe("dg_live_1234567890",
            because: "credentials must always be encrypted before landing in the toggle");
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn403_WhenNonAdminCaller()
    {
        await using var factory = new NonAdminAuthenticatedApiFactory();
        using var client = factory.CreateAuthenticatedClient();

        var body = new { enabled = true, provider = "deepgram", credentials = new { apiKey = "x" } };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn400_WhenProviderNotWhitelisted()
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();

        var body = new { enabled = true, provider = "not-a-real-provider", credentials = new { apiKey = "x" } };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn400_WhenEnabledWithoutCredentials()
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();

        // Deepgram requires apiKey — missing credentials block must fail validation
        var body = new { enabled = true, provider = "deepgram", credentials = (object?)null };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── PUT: live providers and WebSocket endpoints ────────────────────────

    [Theory]
    [InlineData("whisper")]
    [InlineData("azure-whisper")]
    [InlineData("google")]
    public async Task UpdateFeature_ShouldReturn400NamingTheReason_AndKeepState_WhenProviderIsNotLive(string provider)
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();
        var toggle = factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var before = await toggle.GetCurrentAsync();

        var body = new
        {
            enabled = true,
            provider,
            credentials = new { apiKey = "secret-key", endpoint = "https://example.test" },
        };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var messages = json["errors"]!["provider"]!.AsArray().Select(m => m!.GetValue<string>()).ToList();
        messages.Should().ContainSingle().Which.Should().Be(
            $"Provider '{provider}' cannot transcribe a live call: its recognizer returns text only after the audio ends. Live providers: deepgram.");
        json["errors"]!["credentials"].Should().BeNull(
            because: "the provider is refused before the credentials are looked at");

        var after = await toggle.GetCurrentAsync();
        after.Should().Be(before, because: "a refused PUT leaves the stored state unchanged");
    }

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("http://api.deepgram.com")]
    [InlineData("ftp://api.deepgram.com")]
    [InlineData("api.deepgram.com")]
    [InlineData("/v1/listen")]
    [InlineData("wss//api.deepgram.com")]
    public async Task UpdateFeature_ShouldReturn400OnCredentials_AndKeepState_WhenEndpointIsNotAWebSocketUri(string endpoint)
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();
        var toggle = factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var before = await toggle.GetCurrentAsync();

        var body = new
        {
            enabled = true,
            provider = "deepgram",
            credentials = new { apiKey = "secret-key", endpoint },
        };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var messages = json["errors"]!["credentials"]!.AsArray().Select(m => m!.GetValue<string>()).ToList();
        messages.Should().ContainSingle().Which.Should().Be("endpoint must be an absolute ws:// or wss:// URI.");

        var after = await toggle.GetCurrentAsync();
        after.Should().Be(before, because: "a refused PUT leaves the stored state unchanged");
    }

    [Fact]
    public async Task UpdateFeature_ShouldReturn400OnCredentials_WhenDisablingWithAnHttpEndpoint()
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();

        // What the feature page sends to switch off, plus an endpoint it should not carry.
        using var body = new StringContent(
            """{"enabled":false,"provider":null,"credentials":{"apiKey":null,"endpoint":"https://example.test"}}""",
            System.Text.Encoding.UTF8,
            "application/json");
        var response = await client.PutAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["errors"]!["credentials"]!.AsArray().Should().ContainSingle();
    }

    [Theory]
    [InlineData("wss://api.deepgram.com")]
    [InlineData("ws://127.0.0.1:8080/v1/listen")]
    public async Task UpdateFeature_ShouldReturn200WithEndpointMasked_WhenDeepgramEndpointIsAWebSocketUri(string endpoint)
    {
        await using var factory = new AgentAssistTenantScopeApiFactory();
        using var client = factory.PlatformOperatorClient();

        var body = new
        {
            enabled = true,
            provider = "deepgram",
            credentials = new { apiKey = "secret-key", endpoint },
        };
        var response = await client.PutAsJsonAsync("/api/v1/admin/features/agent-assist", body);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        json["enabled"]!.GetValue<bool>().Should().BeTrue();
        json["provider"]!.GetValue<string>().Should().Be("deepgram");
        json["credentialsConfigured"]!.GetValue<bool>().Should().BeTrue();
        json["apiKeyMasked"]!.GetValue<string>().Should().Be("***");
        json["endpointMasked"]!.GetValue<string>().Should().Be("***");
        json["updatedAt"].Should().NotBeNull();
        json["updatedBy"].Should().NotBeNull();

        var toggle = factory.Services.GetRequiredService<IAgentAssistFeatureToggle>();
        var state = await toggle.GetCurrentAsync();
        state.ProtectedCredentials!.Should().ContainKey("endpoint");
        state.ProtectedCredentials!["endpoint"].Should().NotBe(endpoint,
            because: "the endpoint is encrypted like the key");
    }
}

/// <summary>
/// A feature store whose engine-path answer (<c>GetCurrentAsync</c>) differs from the stored state (<c>ReadAsync</c>),
/// as a replica's engine cache does for up to five seconds after another replica's write.
/// </summary>
internal sealed class SplitFeatureStore : IAgentAssistFeatureStore
{
    private static readonly AgentAssistFeatureState Stored = new(
        Enabled: true, Provider: "deepgram", ProtectedCredentials: null, UpdatedAt: DateTimeOffset.UnixEpoch, UpdatedBy: "stored");

    private int _engineReads;

    public int EngineReads => Volatile.Read(ref _engineReads);

    public ValueTask<AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _engineReads);
        return ValueTask.FromResult(AgentAssistFeatureState.Disabled);
    }

    public ValueTask<AgentAssistFeatureState> ReadAsync(CancellationToken ct = default) => ValueTask.FromResult(Stored);

    public ValueTask<AgentAssistFeatureState> ReadForSessionAsync(CancellationToken ct = default) =>
        ValueTask.FromResult(Stored);

    public ValueTask<AgentAssistFeatureState> SetAsync(AgentAssistFeatureState desired, CancellationToken ct = default) =>
        ValueTask.FromResult(desired);
}
