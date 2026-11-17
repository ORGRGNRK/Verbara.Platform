#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup chains .Returns() on ValueTask

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reactive.Subjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.Cluster.Leadership;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.VoiceAi.AudioSocket;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// One Api replica's Agent Assist, composed by the same two extensions <c>Program.cs</c> calls
/// (<see cref="AgentAssistHostingExtensions.AddAgentAssistFeatureStore"/> and
/// <see cref="AgentAssistHostingExtensions.AddPlatformAgentAssist"/>), with the snoop app <c>Program.cs</c> passes
/// (<see cref="StasisAppNames.NodeInstance"/> of the replica's instance id), and the real Pro engine started. The
/// seams the engine reads are the Api's own: the voice-leader activation gate (over a keyed leader whose lease flag
/// the test sets), the Postgres per-call claim when a core database is given, and the snoop settings. Only the edges
/// are stood in for: the session manager's event stream, the node's ARI client (which plays Asterisk's dialplan by
/// connecting one AudioSocket client per continued snoop), the licence, the feature store and the recognizer factory.
/// </summary>
internal sealed class ProEngineReplica : IAsyncDisposable
{
    public const string ServerId = "server-1";
    private const string AudioSocketUuidVariable = "AUDIOSOCKET_UUID";

    /// <summary>Pro's engine type, internal to Pro, read from the assembly of its public supervisor.</summary>
    private static readonly Type Engine = typeof(AgentAssistSupervisor).Assembly.GetType(
        "Verbara.Sdk.Pro.AgentAssist.Engine.AgentAssistEngine", throwOnError: true)!;

    private readonly ServiceProvider _provider;
    private readonly IHostedService _engine;
    private readonly EventStream _events;
    private readonly ConcurrentBag<AudioSocketClient> _clients;

    private ProEngineReplica(
        string instanceId, string snoopApplication, ServiceProvider provider, IHostedService engine, EventStream events,
        IAriChannelsResource channels, ConcurrentBag<AudioSocketClient> clients, Measurements measurements)
    {
        InstanceId = instanceId;
        SnoopApplication = snoopApplication;
        _provider = provider;
        _engine = engine;
        _events = events;
        Channels = channels;
        _clients = clients;
        Measurements = measurements;
        Supervisor = provider.GetRequiredService<AgentAssistSupervisor>();
    }

    public string InstanceId { get; }

    /// <summary>The app the replica's composition gave the engine for its snoops.</summary>
    public string SnoopApplication { get; }

    /// <summary>The node's ARI channels resource: every snoop request the engine made is a received call on it.</summary>
    public IAriChannelsResource Channels { get; }

    /// <summary>Every counter this replica's meters recorded (the engine's and the Api's), by instrument and tags.</summary>
    public Measurements Measurements { get; }

    public AgentAssistSupervisor Supervisor { get; }

    public IServiceProvider Services => _provider;

    /// <summary>The app of each snoop the engine asked the node's ARI client for, in order.</summary>
    public IReadOnlyList<string> SnoopApps => Channels.ReceivedCalls()
        .Where(c => c.GetMethodInfo().Name == nameof(IAriChannelsResource.SnoopAsync))
        .Select(c => (string)c.GetArguments()[1]!)
        .ToList();

    /// <summary>
    /// Composes and starts one replica. <paramref name="isVoiceLeader"/> is the keyed voice AMI leader's lease flag;
    /// <paramref name="core"/> is the core database holding the per-call claims, or <see langword="null"/> for none.
    /// </summary>
    public static async Task<ProEngineReplica> StartAsync(
        string instanceId, bool isVoiceLeader, CallSession call, NpgsqlDataSource? core, CancellationToken ct)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentAssist:AudioSocket:AdvertisedHost"] = "127.0.0.1",
            ["AgentAssist:AudioSocket:Port"] = "9093",
        }).Build();
        var events = new EventStream();
        var sessions = Substitute.For<ICallSessionManager>();
        sessions.Events.Returns(events);
        sessions.GetById(call.SessionId).Returns(call);
        var audio = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        var clients = new ConcurrentBag<AudioSocketClient>();
        var channels = Substitute.For<IAriChannelsResource>();
        var leader = Substitute.For<IClusterLeader>();
        leader.IsLeader.Returns(isVoiceLeader);
        leader.Resource.Returns(VoiceLeaderResources.AmiOwner);
        var store = new CountingFeatureStore(AgentAssistFeatureState.Disabled);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton(sessions);
        services.AddSingleton(PoolWithAri(channels, audio, clients));
        services.AddSingleton(audio);                                  // the engine's TryAdd keeps this one
        services.AddSingleton<ILicenseGuard>(new AllowEverything());   // AddProLicenseGuard's TryAdd keeps this one
        services.AddSingleton<ISpeechRecognizerFactory>(                // AddAgentAssistRecognizer's TryAdd keeps this one
            new RecordingRecognizerFactory(RecordedStreamEnd.WaitForCancellation));
        services.AddKeyedSingleton(VoiceLeaderResources.AmiOwner, leader);
        services.Configure<ClusterLeadershipOptions>(o => o.InstanceId = instanceId);

        // What Program.cs calls, with the app it passes, then this test's feature store over the one registered.
        var snoopApplication = StasisAppNames.NodeInstance(configuration, instanceId);
        services.AddAgentAssistFeatureStore(configuration, core);
        services.AddPlatformAgentAssist(configuration, snoopApplication, core);
        services.AddSingleton<IAgentAssistFeatureStore>(store);

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var protector = provider.GetRequiredService<AgentAssistCredentialsProtector>();
        store.State = new AgentAssistFeatureState(
            true, "deepgram", protector.Protect(new Dictionary<string, string> { ["apiKey"] = "dg-seam-key" }),
            DateTimeOffset.UtcNow, "admin");
        var measurements = new Measurements(provider.GetRequiredService<IMeterFactory>());

        // Only the engine runs: the bridge Program.cs hosts beside it is not under test here.
        var engine = (IHostedService)provider.GetRequiredService(Engine);
        await engine.StartAsync(ct);
        await events.Subscribed.WaitAsync(ct); // the engine subscribes once its AudioSocket server listens

        return new ProEngineReplica(instanceId, snoopApplication, provider, engine, events, channels, clients, measurements);
    }

    /// <summary>The session manager reports the call connected to an agent.</summary>
    public void Connect(CallSession call) =>
        _events.OnNext(new CallConnectedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, "agent-1", "support", TimeSpan.Zero));

    /// <summary>The session manager reports the call connected, and the engine announces a session for it.</summary>
    public async Task ConnectAndAwaitSessionAsync(CallSession call, CancellationToken ct)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = Supervisor.SessionStarted.Subscribe(s =>
        {
            if (s.SessionId == call.SessionId)
                started.TrySetResult();
        });
        Connect(call);
        await started.Task.WaitAsync(ct);
    }

    /// <summary>The session manager reports the call ended, and the engine ends its session.</summary>
    public async Task EndAndAwaitSessionEndAsync(CallSession call, CancellationToken ct)
    {
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = Supervisor.SessionEnded.Subscribe(s =>
        {
            if (s.SessionId == call.SessionId)
                ended.TrySetResult();
        });
        _events.OnNext(new CallEndedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, null, TimeSpan.FromSeconds(1), null));
        await ended.Task.WaitAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.StopAsync(CancellationToken.None);
        foreach (var client in _clients)
            await client.DisposeAsync();
        Measurements.Dispose();
        await _provider.DisposeAsync();
        _events.Dispose();
    }

    /// <summary>
    /// A node whose ARI client answers the snoop requests (whatever app they name; the tests read it from the received
    /// calls) and plays Asterisk's dialplan: continuing a snoop connects one AudioSocket client to
    /// <paramref name="audio"/> with the id the engine set on that snoop.
    /// </summary>
    private static VerbaraServerPool PoolWithAri(
        IAriChannelsResource channels, AudioSocketServer audio, ConcurrentBag<AudioSocketClient> clients)
    {
        var uuids = new ConcurrentDictionary<string, Guid>(StringComparer.Ordinal);
        var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        var ami = Substitute.For<IAmiConnection>();
        ami.State.Returns(AmiConnectionState.Connected);
        var server = new VerbaraServer(ami, NullLogger<VerbaraServer>.Instance);

        channels.SnoopAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => ValueTask.FromResult(new AriChannel { Id = c.ArgAt<string?>(4) ?? "snoop" }));
        channels.SetVariableAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                if (c.ArgAt<string>(1) == AudioSocketUuidVariable)
                    uuids[c.ArgAt<string>(0)] = Guid.Parse(c.ArgAt<string>(2));
                return ValueTask.CompletedTask;
            });
        channels.ContinueAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => new ValueTask(ConnectAsync(uuids[c.ArgAt<string>(0)], c.ArgAt<CancellationToken>(5))));
        channels.HangupAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        var ari = Substitute.For<IAriClient>();
        ari.Channels.Returns(channels);

        server.SetAriClient(ari);
        pool.AddExistingServer(ServerId, server);
        return pool;

        async Task ConnectAsync(Guid id, CancellationToken ct)
        {
            var client = new AudioSocketClient("127.0.0.1", audio.BoundPort, id);
            clients.Add(client);
            await client.ConnectAsync(ct);
        }
    }

    /// <summary>The session-event stream, with a signal for the moment the engine subscribes to it.</summary>
    private sealed class EventStream : IObservable<SessionDomainEvent>, IDisposable
    {
        private readonly Subject<SessionDomainEvent> _events = new();
        private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Subscribed => _subscribed.Task;

        public IDisposable Subscribe(IObserver<SessionDomainEvent> observer)
        {
            var subscription = _events.Subscribe(observer);
            _subscribed.TrySetResult();
            return subscription;
        }

        public void OnNext(SessionDomainEvent value) => _events.OnNext(value);

        public void Dispose() => _events.Dispose();
    }

    private sealed class AllowEverything : ILicenseGuard
    {
        public ValueTask<LicenseGuardResult> CanExecuteAsync(LicenseFeature feature, CancellationToken ct) =>
            ValueTask.FromResult(CanExecute(feature));

        public LicenseGuardResult CanExecute(LicenseFeature feature) => new(Allowed: true, Reason: null);
    }
}

/// <summary>
/// The <see cref="long"/> counters recorded on the meters of one container's <see cref="IMeterFactory"/>, so two
/// replicas in one process, and other test classes running at the same time, never see each other's measurements.
/// </summary>
internal sealed class Measurements : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly Lock _lock = new();
    private readonly List<Measured> _recorded = [];
    private readonly List<(Func<Measured, bool> Match, TaskCompletionSource Seen)> _waiters = [];

    public Measurements(IMeterFactory factory)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, factory))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagMap = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var tag in tags)
                tagMap[tag.Key] = tag.Value?.ToString();
            var measured = new Measured(instrument.Name, value, tagMap);
            List<TaskCompletionSource> seen = [];
            lock (_lock)
            {
                _recorded.Add(measured);
                foreach (var waiter in _waiters.Where(w => w.Match(measured)).ToList())
                {
                    seen.Add(waiter.Seen);
                    _waiters.Remove(waiter);
                }
            }

            foreach (var tcs in seen)
                tcs.TrySetResult();
        });
        _listener.Start();
    }

    public IReadOnlyList<Measured> Recorded
    {
        get
        {
            lock (_lock)
                return [.. _recorded];
        }
    }

    /// <summary>Completes once a measurement matching <paramref name="match"/> was recorded (already or later).</summary>
    public Task SeenAsync(Func<Measured, bool> match)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_recorded.Any(match))
                return Task.CompletedTask;
            _waiters.Add((match, seen));
        }

        return seen.Task;
    }

    public long Sum(string instrument, string? tagKey = null, string? tagValue = null)
    {
        lock (_lock)
        {
            return _recorded
                .Where(m => m.Instrument == instrument
                    && (tagKey is null || (m.Tags.TryGetValue(tagKey, out var v) && v == tagValue)))
                .Sum(m => m.Value);
        }
    }

    public void Dispose() => _listener.Dispose();

    internal sealed record Measured(string Instrument, long Value, IReadOnlyDictionary<string, string?> Tags)
    {
        public bool Is(string instrument, string tagKey, string tagValue) =>
            Instrument == instrument && Tags.TryGetValue(tagKey, out var v) && v == tagValue;
    }
}
