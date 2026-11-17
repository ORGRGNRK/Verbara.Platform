using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Verbara.Platform.Api.Endpoints;
using Verbara.Platform.Core;
using Verbara.Platform.Core.Push;

namespace Verbara.Platform.Api.Tests.Sse;

/// <summary>
/// agent-assist-works-end-to-end (design D12, sending half): the four <c>agentassist.*</c> records cross pods in the
/// backplane payload. Serialized as the relay serializes them (the runtime type, with the
/// <see cref="PlatformPushJsonContext"/> options the Api configures) and decoded as the receiver decodes them, every member
/// comes back equal, <c>timestamp</c> included, and the SSE frame written from the decoded copy is byte-identical to the
/// frame written from the original.
/// </summary>
public sealed class AgentAssistCrossPodWireTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 3, 10, 43, 0, 120, TimeSpan.Zero);

    /// <summary>The options the Api hands the relay (<c>PushProOptions.PayloadSerializerOptions</c>).</summary>
    internal static readonly JsonSerializerOptions BackplaneOptions = new() { TypeInfoResolver = PlatformPushJsonContext.Default };

    public static TheoryData<string> Types() => new()
    {
        "agentassist.suggestion",
        "agentassist.sentiment",
        "agentassist.compliance_alert",
        "agentassist.transcript",
    };

    internal static PlatformEvent Event(string type, string tenant = "demo", string agentId = "a-9e8d7c6b5a493827") => type switch
    {
        "agentassist.suggestion" => new AgentAssistSuggestionEvent(tenant, "as-5c4b3a29", agentId,
            "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", "sg-01", "Offer to resend the tracking number.", "Important", "Keyword", "card")
            { Timestamp = At },
        "agentassist.sentiment" => new AgentAssistSentimentEvent(tenant, "as-5c4b3a29", agentId,
            "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", "Caller", -0.62f, "Frustrated", ["again", "third time"]) { Timestamp = At },
        "agentassist.compliance_alert" => new AgentAssistComplianceAlertEvent(tenant, "as-5c4b3a29", agentId,
            "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", "cr-identity-check", "full card number", "Warning") { Timestamp = At },
        "agentassist.transcript" => new AgentAssistTranscriptEvent(tenant, "as-5c4b3a29", agentId,
            "4f1d2b8c9a0e4d7f8c6b5a4e3d2c1b0a", "Caller", "This is the third time I am calling.", IsFinal: true)
            { Timestamp = At },
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The backplane payload of <paramref name="evt"/>, as <c>RedisEventRelay.SerializePayload</c> writes it.</summary>
    internal static string Payload(PlatformEvent evt) => JsonSerializer.Serialize(evt, evt.GetType(), BackplaneOptions);

    private static PlatformEvent Decode(PlatformEvent original) =>
        (PlatformEvent)JsonSerializer.Deserialize(Payload(original), PlatformPushJsonContext.Default.GetTypeInfo(original.GetType())!)!;

    [Theory]
    [MemberData(nameof(Types))]
    public void Decode_ShouldGiveBackEveryMember_WhenTheRecordCrossesTheBackplane(string type)
    {
        var original = Event(type);

        var decoded = Decode(original);

        decoded.Should().BeOfType(original.GetType());
        decoded.Should().BeEquivalentTo(original, o => o.ComparingRecordsByMembers().RespectingRuntimeTypes(),
            "every member, timestamp included, is carried by the payload");
        decoded.Timestamp.Should().Be(At);
        ((IConversationContentEvent)decoded).Audience.Should().Be(((IConversationContentEvent)original).Audience);
    }

    [Theory]
    [MemberData(nameof(Types))]
    public async Task WriteEventAsync_ShouldWriteTheSameFrame_WhenTheRecordWasDecodedFromTheBackplane(string type)
    {
        var original = Event(type);

        var fromDecoded = await SseFrame(Decode(original));

        fromDecoded.Should().Be(await SseFrame(original), "an agent on another replica reads the same bytes");
    }

    private static async Task<string> SseFrame(PlatformEvent evt)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await SseEndpoints.WriteEventAsync(context.Response, evt.Type, evt, CancellationToken.None);

        return Encoding.UTF8.GetString(body.ToArray());
    }
}
