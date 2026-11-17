using Verbara.Sdk.Ari.Client;

namespace Verbara.Platform.Api.Services;

/// <summary>
/// The two Stasis application names the Api registers on Asterisk, resolved in one place (Platform/ADR-0044,
/// spec <c>voice-stasis-app-ownership</c>). Asterisk hands a Stasis app to exactly one ARI WebSocket, the last one
/// to register it, so the dialplan's app (<see cref="Consumer"/>) is registered only by the inbound-voice leader's
/// <see cref="StasisInboundConsumer"/>, and the per-node ARI client of the cluster's Asterisk node registers an app
/// of its own on each replica (<see cref="NodeInstance"/>), which nothing in the dialplan targets.
/// </summary>
internal static class StasisAppNames
{
    /// <summary>The dialplan's app: <c>Stasis(verbara,…)</c> in <c>extensions.conf</c>.</summary>
    public const string ApplicationKey = "Asterisk:Ari:Application";

    /// <summary>The prefix of the per-node ARI client's app (<see cref="NodeInstance"/>).</summary>
    public const string NodeApplicationKey = "Asterisk:Ari:NodeApplication";

    private const string BaseUrlKey = "Asterisk:Ari:BaseUrl";
    private const string DefaultApplication = "verbara";
    private const string NodeSuffix = "-node";

    /// <summary><c>Asterisk:Ari:Application</c>, default <c>verbara</c>.</summary>
    public static string Consumer(IConfiguration configuration) =>
        NonBlank(configuration[ApplicationKey]) ?? DefaultApplication;

    /// <summary>
    /// <c>Asterisk:Ari:NodeApplication</c>, default the dialplan's app followed by <c>-node</c>: the prefix of the app
    /// each replica's node client registers (<see cref="NodeInstance"/>).
    /// </summary>
    public static string Node(IConfiguration configuration) =>
        NonBlank(configuration[NodeApplicationKey]) ?? Consumer(configuration) + NodeSuffix;

    /// <summary>
    /// The app this replica's node ARI client registers: <see cref="Node"/> followed by <c>-</c> and
    /// <paramref name="instanceId"/> (the replica's machine name), so every replica holds an app of its own. Agent
    /// Assist creates its snoops in this app, so a snoop is always born in the app of the replica that created it.
    /// </summary>
    public static string NodeInstance(IConfiguration configuration, string instanceId) =>
        Node(configuration) + "-" + instanceId;

    /// <summary>
    /// The per-node ARI client options, registering <see cref="NodeInstance"/>, or <see langword="null"/> when ARI is
    /// not configured. Throws when the configured node app equals the dialplan's app, or when the name the node client
    /// registers does (both compared without regard to case), so the race ADR-0044 closes cannot come back through
    /// configuration. <c>Program.cs</c> calls this eagerly, before the host is built.
    /// </summary>
    public static AriClientOptions? NodeOptions(IConfiguration configuration, string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var options = Options(configuration, NodeInstance(configuration, instanceId));
        if (options is null)
            return null;

        var consumer = Consumer(configuration);
        if (string.Equals(Node(configuration), consumer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{NodeApplicationKey} ('{Node(configuration)}') must differ from {ApplicationKey} " +
                $"('{consumer}'): only the inbound-voice leader may register the dialplan's Stasis app " +
                "(Platform/ADR-0044). Unset Asterisk:Ari:NodeApplication to use the default '<Application>-node'.");
        if (string.Equals(options.Application, consumer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The node app this replica registers, {NodeApplicationKey} ('{Node(configuration)}') followed by " +
                $"'-' and the instance id '{instanceId}', is '{options.Application}', which equals {ApplicationKey} " +
                $"('{consumer}'): only the inbound-voice leader may register the dialplan's Stasis app " +
                "(Platform/ADR-0044). Choose an Asterisk:Ari:NodeApplication that is not a prefix of Asterisk:Ari:Application.");
        return options;
    }

    /// <summary>The Stasis consumer's ARI client options, or <see langword="null"/> when ARI is not configured.</summary>
    public static AriClientOptions? ConsumerOptions(IConfiguration configuration) =>
        Options(configuration, Consumer(configuration));

    private static AriClientOptions? Options(IConfiguration configuration, string application)
    {
        var baseUrl = NonBlank(configuration[BaseUrlKey]);
        if (baseUrl is null)
            return null;

        return new AriClientOptions
        {
            BaseUrl = baseUrl,
            Username = configuration["Asterisk:Ari:Username"] ?? "",
            Password = configuration["Asterisk:Ari:Password"] ?? "",
            Application = application,
        };
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
