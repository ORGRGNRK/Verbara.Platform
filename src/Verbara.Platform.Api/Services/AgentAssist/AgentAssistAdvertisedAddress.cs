using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Verbara.Platform.Core.Configuration;
using SnoopOptions = Verbara.Sdk.Pro.AgentAssist.Options.AgentAssistOptions;

namespace Verbara.Platform.Api.Services.AgentAssist;

/// <summary>
/// Supplies the engine's <see cref="SnoopOptions.AudioSocketAdvertisedAddress"/>, the <c>host:port</c> Asterisk sends a
/// snoop's audio to, from <c>AgentAssist:AudioSocket:AdvertisedHost</c> and <c>:Port</c>, at first use, never at host
/// start.
/// </summary>
/// <remarks>
/// <para>
/// A literal host is used as given (an IPv6 address goes in brackets). <c>auto</c> is the local address of this
/// replica's route to the ARI host (<c>Asterisk:Ari:BaseUrl</c>): a UDP socket is connected to that host, which sends no
/// packet, and its local endpoint names this replica only, never a service name that resolves to every replica.
/// </para>
/// <para>
/// Nothing here can fail a boot: with no host, an <c>auto</c> that finds no route or an unusable literal, the address
/// stays unset, the engine skips each call and counts it (<c>agentassist.sessions.skipped</c>, reason
/// <c>no_advertised_address</c>), a Warning is logged once, and the resolution is tried again at most once per
/// <see cref="RetryInterval"/>. Once set, the address is kept for the life of the process.
/// </para>
/// </remarks>
internal sealed partial class AgentAssistAdvertisedAddress : IDisposable
{
    /// <summary>The shortest time between two failed resolutions.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    /// <summary>The bound on one name lookup of the ARI host.</summary>
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(2);

    private const string AriBaseUrlKey = "Asterisk:Ari:BaseUrl";

    private readonly SnoopOptions _snoop;
    private readonly string? _host;
    private readonly int _port;
    private readonly string? _ariBaseUrl;
    private readonly ILogger<AgentAssistAdvertisedAddress> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _resolving = new(1, 1);
    private long _nextAttempt;
    private int _warned;

    public AgentAssistAdvertisedAddress(
        SnoopOptions snoop,
        IOptions<AgentAssistOptions> options,
        IConfiguration configuration,
        ILogger<AgentAssistAdvertisedAddress> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(snoop);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);
        _snoop = snoop;
        var audio = options.Value.AudioSocket;
        _host = string.IsNullOrWhiteSpace(audio.AdvertisedHost) ? null : audio.AdvertisedHost.Trim();
        _port = audio.Port;
        _ariBaseUrl = configuration[AriBaseUrlKey];
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Sets the engine's advertised address when it is not set yet and a host is configured. Never throws, except an
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        if (_host is null || _snoop.AudioSocketAdvertisedAddress is not null)
            return;
        if (_time.GetTimestamp() < Volatile.Read(ref _nextAttempt))
            return;

        await _resolving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_snoop.AudioSocketAdvertisedAddress is not null || _time.GetTimestamp() < Volatile.Read(ref _nextAttempt))
                return;

            var (address, reason) = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            if (address is null)
            {
                Volatile.Write(ref _nextAttempt, _time.GetTimestamp() + (long)(RetryInterval.TotalSeconds * _time.TimestampFrequency));
                if (Interlocked.Exchange(ref _warned, 1) == 0)
                    LogUnresolved(_logger, _host, reason);
                return;
            }

            _snoop.AudioSocketAdvertisedAddress = address;
            LogResolved(_logger, address, _host);
        }
        finally
        {
            _resolving.Release();
        }
    }

    private async ValueTask<(string? Address, string Reason)> ResolveAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(_host, AgentAssistAudioSocketOptions.AutoAdvertisedHost, StringComparison.OrdinalIgnoreCase))
        {
            return Format(_host!, _port) is { } literal
                ? (literal, "")
                : (null, "the host is not a host name or an IP address");
        }

        if (!Uri.TryCreate(_ariBaseUrl, UriKind.Absolute, out var ari) || string.IsNullOrEmpty(ari.Host))
            return (null, $"'auto' needs {AriBaseUrlKey} to find the route to Asterisk");

        try
        {
            var local = await LocalAddressTowardsAsync(ari.IdnHost, ari.Port, cancellationToken).ConfigureAwait(false);
            return local is null
                ? (null, $"no route to the ARI host '{ari.Host}'")
                : (Format(local, _port), "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (null, $"no route to the ARI host '{ari.Host}' ({ex.GetType().Name})");
        }
    }

    /// <inheritdoc />
    public void Dispose() => _resolving.Dispose();

    /// <summary>The local address of this host's route to <paramref name="host"/>, or <see langword="null"/>.</summary>
    internal static async ValueTask<IPAddress?> LocalAddressTowardsAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] candidates;
        if (IPAddress.TryParse(host, out var literal))
        {
            candidates = [literal];
        }
        else
        {
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lookup.CancelAfter(LookupTimeout);
            candidates = await Dns.GetHostAddressesAsync(host, lookup.Token).ConfigureAwait(false);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                // Connecting a UDP socket only selects the route; it sends nothing.
                using var probe = new Socket(candidate.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                probe.Connect(new IPEndPoint(candidate, port));
                if (probe.LocalEndPoint is IPEndPoint { Address: var local } && !local.Equals(IPAddress.Any)
                    && !local.Equals(IPAddress.IPv6Any))
                {
                    return local;
                }
            }
            catch (SocketException)
            {
                // No route through this address family; try the next candidate.
            }
        }

        return null;
    }

    /// <summary>
    /// <c>host:port</c> as the engine accepts it: an IPv6 address in brackets, a host name or IPv4 address as given; or
    /// <see langword="null"/> when <paramref name="host"/> is neither.
    /// </summary>
    internal static string? Format(string host, int port)
    {
        if (port is < 1 or > 65535)
            return null;
        var bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out var ip))
            return Format(ip, port);
        if (bare.Length == 0 || bare.Any(c => char.IsWhiteSpace(c) || c is ':' or '[' or ']' or '/'))
            return null;
        return bare + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    private static string Format(IPAddress address, int port)
    {
        var text = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
        return text + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Agent Assist sends snoop audio to {Address} (AgentAssist:AudioSocket:AdvertisedHost '{Host}')")]
    private static partial void LogResolved(ILogger logger, string address, string host);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Agent Assist has no AudioSocket address yet (AgentAssist:AudioSocket:AdvertisedHost '{Host}'): {Reason}. Each call is skipped until it resolves; retried at most every 30 s (logged once)")]
    private static partial void LogUnresolved(ILogger logger, string host, string reason);
}

/// <summary>
/// The feature toggle the engine reads at each connected call: the stored toggle, and, while it is on, the advertised
/// address resolved at that first use (<see cref="AgentAssistAdvertisedAddress"/>). The engine reads the toggle before
/// it reads the address, so the first call that needs the address finds it set.
/// </summary>
internal sealed class AgentAssistEngineToggle(
    Verbara.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle toggle,
    AgentAssistAdvertisedAddress address) : Verbara.Sdk.Pro.AgentAssist.Features.IAgentAssistFeatureToggle
{
    /// <inheritdoc />
    public async ValueTask<Verbara.Sdk.Pro.AgentAssist.Features.AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default)
    {
        var state = await toggle.GetCurrentAsync(ct).ConfigureAwait(false);
        if (state.Enabled)
            await address.EnsureAsync(ct).ConfigureAwait(false);
        return state;
    }

    /// <inheritdoc />
    public ValueTask<Verbara.Sdk.Pro.AgentAssist.Features.AgentAssistFeatureState> SetAsync(
        Verbara.Sdk.Pro.AgentAssist.Features.AgentAssistFeatureState desired, CancellationToken ct = default) =>
        toggle.SetAsync(desired, ct);
}
