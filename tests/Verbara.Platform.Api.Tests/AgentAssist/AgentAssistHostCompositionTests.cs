#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup chains .Returns() on ValueTask

using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.Services;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Tests.Conversations;
using Verbara.Platform.Core;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.AudioSocket;
using SnoopOptions = Verbara.Sdk.Pro.AgentAssist.Options.AgentAssistOptions;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end, task B.4 (design D3, D7; spec audit round 8, F1): the Agent Assist block of the real
/// <c>Program</c>, booted under <c>Testing</c>. The engine and the bridge exist only where the engine's stores do (a
/// <c>ConnectionStrings:Analytics</c>; the event store and the realtime store migrate it at boot, so it is a Postgres
/// container shared by the class).
/// </summary>
/// <remarks>
/// Every Verbara hosted service is replaced by a <see cref="HostedProbe"/> at the end of the list, in the order
/// <c>Program.cs</c> registered them, so the resolved <see cref="IEnumerable{IHostedService}"/> keeps that order and
/// names what each one is. A probe starts nothing, except the bridge and the engine in the tests that run them.
/// </remarks>
public sealed class AgentAssistHostCompositionTests(ConversationClockReconcileLiveDbTests.PostgresContainer postgres)
    : IClassFixture<ConversationClockReconcileLiveDbTests.PostgresContainer>
{
    private readonly string _analytics = postgres.ConnectionStringFor("postgres");
    private const string ServerId = "server-1";
    private const string AudioSocketUuidVariable = "AUDIOSOCKET_UUID";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(15);

    /// <summary>Pro's engine type, internal to Pro, read from the assembly of its public supervisor.</summary>
    private static readonly Type Engine = typeof(AgentAssistSupervisor).Assembly.GetType(
        "Verbara.Sdk.Pro.AgentAssist.Engine.AgentAssistEngine", throwOnError: true)!;
    private static readonly Type Bridge = typeof(AgentAssistBridge);

    [Fact]
    public void Boot_ShouldHostTheBridge_WhenTheAnalyticsConnectionExists()
    {
        using var host = new AssistHost(new Dictionary<string, string?> { ["ConnectionStrings:Analytics"] = _analytics });

        host.HostedTypes.Should().Contain(Bridge).And.Contain(Engine);
    }

    [Fact]
    public void Boot_ShouldHostNeitherTheBridgeNorTheEngine_WhenThereIsNoAnalyticsConnection()
    {
        using var host = new AssistHost(new Dictionary<string, string?>());

        host.HostedTypes.Should().NotContain(Bridge).And.NotContain(Engine);
    }

    [Fact]
    public void Boot_ShouldStartTheBridgeBeforeTheEngine()
    {
        using var host = new AssistHost(new Dictionary<string, string?> { ["ConnectionStrings:Analytics"] = _analytics });

        var order = host.HostedTypes;
        var bridge = order.IndexOf(Bridge);
        var engine = order.IndexOf(Engine);

        bridge.Should().BeGreaterThanOrEqualTo(0, "the bridge is hosted where the engine is");
        engine.Should().BeGreaterThan(bridge,
            "the host starts hosted services in this order, and the bridge must subscribe before the engine can announce");
    }

    [Fact]
    public void Boot_ShouldStartTheEngine_WhenAgentAssistIsOnAndNoAdvertisedHostIsSet()
    {
        var audio = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        using var host = new AssistHost(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Analytics"] = _analytics,
                ["AgentAssist:Enabled"] = "true",
                ["AgentAssist:Provider"] = "deepgram",
            },
            runAssist: true,
            configure: services => services.Replace(ServiceDescriptor.Singleton(audio)));

        var started = host.Probes.Where(p => p.Started).Select(p => p.Type).ToList();

        started.Should().Contain(Engine).And.Contain(Bridge);
        var snoop = host.Services.GetRequiredService<SnoopOptions>();
        snoop.AudioSocketAdvertisedAddress.Should().BeNull("no host is configured, so each call is skipped, not the boot");
        snoop.SnoopApplication.Should().Be("verbara-node-" + Environment.MachineName);
    }

    /// <summary>
    /// The startup window, widened: whichever of the bridge and the engine starts first is followed by a step that
    /// holds the start until released (a hosted service between the two). Inside it a call connects and, when the
    /// engine started first, the hold lasts until the engine has announced its session. With the bridge first,
    /// the engine starts after the release and announces to a bridge that is listening; with the engine first, the
    /// session is announced before the bridge subscribed, and the bridge never forwards it.
    /// </summary>
    [Fact]
    public async Task Startup_ShouldForwardASessionTheEngineAnnouncesWhileTheHostIsStillStarting()
    {
        var call = new CallSession("aa-startup-window", "1712345678.30", ServerId, CallDirection.Inbound) { TenantId = "demo" };
        var monitor = new MemoryLiveSessionStore(TimeProvider.System);
        using var events = new ReplaySignal();
        var audio = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
        var clients = new ConcurrentBag<AudioSocketClient>();
        var recognized = new RecognizerRelease();
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = new SingleAssignmentDisposable();

        var window = new StartupWindow(async (provider, first) =>
        {
            watch.Disposable = provider.GetRequiredService<AgentAssistSupervisor>().SessionStarted.Subscribe(s =>
            {
                if (s.SessionId == call.SessionId)
                    announced.TrySetResult();
            });
            events.OnNext(new CallConnectedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, "agent-1", "support", TimeSpan.Zero));
            if (first == Engine)
                await announced.Task.WaitAsync(Bound); // the engine runs already: hold until it has announced
        });

        using var host = new AssistHost(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Analytics"] = _analytics,
                ["AgentAssist:Enabled"] = "true",
                ["AgentAssist:Provider"] = "deepgram",
                ["AgentAssist:AudioSocket:AdvertisedHost"] = "127.0.0.1",
            },
            runAssist: true,
            window: window,
            configure: services =>
            {
                var sessions = Substitute.For<ICallSessionManager>();
                sessions.Events.Returns(events);
                sessions.GetById(call.SessionId).Returns(call);
                services.Replace(ServiceDescriptor.Singleton(sessions));
                services.Replace(ServiceDescriptor.Singleton(PoolWithAri(audio, clients)));
                services.Replace(ServiceDescriptor.Singleton(audio));
                services.Replace(ServiceDescriptor.Singleton<ILicenseGuard>(new AllowEverything()));
                services.Replace(ServiceDescriptor.Singleton<IAgentAssistActivationGate>(new AlwaysActive()));
                services.RemoveAll<SpeechRecognizer>();
                services.AddSingleton(recognized);
                services.AddTransient<SpeechRecognizer, ReleasedRecognizer>();
                services.Replace(ServiceDescriptor.Singleton<IAgentAssistLiveSessionStore>(monitor));   // a shared list
            });

        try
        {
            _ = host.Services; // starts the host: the bridge, the window and the engine, in Program.cs's order
            await announced.Task.WaitAsync(Bound);

            var forwarded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var bus = host.Services.GetRequiredService<PlatformEventBus>().Events
                .OfType<AgentAssistTranscriptEvent>()
                .Where(e => e.SessionId == call.SessionId)
                .Subscribe(_ => forwarded.TrySetResult());
            recognized.Release();

            var inTime = await forwarded.Task.WaitAsync(TimeSpan.FromSeconds(5))
                .ContinueWith(t => t.IsCompletedSuccessfully, TaskScheduler.Default);
            inTime.Should().BeTrue(
                "the bridge must forward the transcript of a session announced during startup; it did not: " +
                "session announced before the bridge subscribed");
            (await monitor.ListAsync("demo", CancellationToken.None)).Select(r => r.SessionId).Should().Equal(
                [call.SessionId], "the bridge wrote the Monitor record of the session announced during startup (design D12)");
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    /// <summary>The real <c>Program</c> with every Verbara hosted service behind a <see cref="HostedProbe"/>.</summary>
    private sealed class AssistHost(
        IReadOnlyDictionary<string, string?> settings,
        bool runAssist = false,
        StartupWindow? window = null,
        Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        public IReadOnlyList<HostedProbe> Probes => Services.GetServices<IHostedService>().OfType<HostedProbe>().ToList();

        public List<Type?> HostedTypes => Probes.Select(p => p.Type).ToList();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in settings)
                builder.UseSetting(key, value);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                // The descriptors StubVerbaraHostedServices removes, in Program.cs's order.
                var hosted = services.Where(d => d.ServiceType == typeof(IHostedService) &&
                    (d.ImplementationType?.FullName?.Contains("Asterisk") == true ||
                     d.ImplementationType?.FullName?.Contains("Verbara") == true ||
                     d.ImplementationFactory is not null)).ToList();
                // Pro registers the engine as a singleton, then as a hosted service forwarding to it, one after the other.
                var all = services.ToList();
                var engineAt = all.FindIndex(d => d.ServiceType == Engine);
                var engineHosted = engineAt < 0 ? null : all.Skip(engineAt + 1)
                    .FirstOrDefault(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null);
                AuthenticatedPlatformApiFactory.StubVerbaraHostedServices(services);
                AuthenticatedPlatformApiFactory.RegisterInMemoryStores(services);
                services.AddAllProFeaturesLicensed();
                if (!services.Any(d => d.ServiceType == typeof(byte[])))
                    services.AddSingleton<byte[]>([]);
                configure?.Invoke(services);
                foreach (var descriptor in hosted)
                    services.AddSingleton<IHostedService>(sp =>
                        HostedProbe.For(sp, descriptor, ReferenceEquals(descriptor, engineHosted), runAssist, window));
            });
            return base.CreateHost(builder);
        }
    }

    /// <summary>
    /// Stands in for one hosted service <c>Program.cs</c> registered and names it (<see cref="Type"/>). Only the bridge
    /// and the engine are built and run, when asked; any other is neither built from its type nor started.
    /// </summary>
    private sealed class HostedProbe(Type? type, IHostedService? inner, StartupWindow? window) : IHostedService
    {
        /// <summary>The service's type; <see langword="null"/> for a factory registration other than the engine's.</summary>
        public Type? Type { get; } = type;

        public bool Started { get; private set; }

        public static HostedProbe For(
            IServiceProvider provider, ServiceDescriptor descriptor, bool isEngine, bool run, StartupWindow? window)
        {
            if (window is not null)
                window.Provider ??= provider;

            if (descriptor.ImplementationType == typeof(AgentAssistBridge))
                return new HostedProbe(Bridge, run ? ActivatorUtilities.CreateInstance<AgentAssistBridge>(provider) : null, window);

            if (isEngine)
            {
                var engine = (IHostedService)descriptor.ImplementationFactory!(provider);
                engine.GetType().Should().Be(Engine, "the hosted service after the engine's singleton is the engine");
                return new HostedProbe(Engine, run ? engine : null, window);
            }

            return new HostedProbe(descriptor.ImplementationType, null, null);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (inner is null)
                return;
            await inner.StartAsync(cancellationToken);
            Started = true;
            if (window is not null)
                await window.AfterStartAsync(Type!);
        }

        public Task StopAsync(CancellationToken cancellationToken) =>
            inner?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>
    /// The step that holds the host's start after the first of the bridge and the engine has started, as a hosted service
    /// registered between the two would, until its action completes.
    /// </summary>
    private sealed class StartupWindow(Func<IServiceProvider, Type, Task> hold)
    {
        private int _started;

        public IServiceProvider? Provider { get; set; }

        /// <summary>Runs the hold after the first of the two has started, naming it.</summary>
        public Task AfterStartAsync(Type started) =>
            Interlocked.Increment(ref _started) == 1 ? hold(Provider!, started) : Task.CompletedTask;
    }

    /// <summary>The session-event stream: replays what was raised before the engine subscribed (the engine subscribes
    /// once its AudioSocket server listens, which may be after its start returned).</summary>
    private sealed class ReplaySignal : IObservable<SessionDomainEvent>, IDisposable
    {
        private readonly ReplaySubject<SessionDomainEvent> _events = new();

        public IDisposable Subscribe(IObserver<SessionDomainEvent> observer) => _events.Subscribe(observer);

        public void OnNext(SessionDomainEvent value) => _events.OnNext(value);

        public void Dispose() => _events.Dispose();
    }

    /// <summary>
    /// A node whose ARI client answers the snoop requests and plays Asterisk's dialplan: continuing a snoop connects one
    /// AudioSocket client to <paramref name="audio"/> with the id the engine set on that snoop.
    /// </summary>
    private static VerbaraServerPool PoolWithAri(AudioSocketServer audio, ConcurrentBag<AudioSocketClient> clients)
    {
        var uuids = new ConcurrentDictionary<string, Guid>(StringComparer.Ordinal);
        var pool = new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance);
        var ami = Substitute.For<IAmiConnection>();
        ami.State.Returns(AmiConnectionState.Connected);
        var server = new VerbaraServer(ami, NullLogger<VerbaraServer>.Instance);

        var channels = Substitute.For<IAriChannelsResource>();
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

    private sealed class AllowEverything : ILicenseGuard
    {
        public ValueTask<LicenseGuardResult> CanExecuteAsync(LicenseFeature feature, CancellationToken ct) =>
            ValueTask.FromResult(CanExecute(feature));

        public LicenseGuardResult CanExecute(LicenseFeature feature) => new(Allowed: true, Reason: null);
    }

    private sealed class AlwaysActive : IAgentAssistActivationGate
    {
        public bool IsActive => true;
    }

    /// <summary>Holds every recognizer of the test until <see cref="Release"/>.</summary>
    private sealed class RecognizerRelease
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _released.Task;

        public void Release() => _released.TrySetResult();
    }

    /// <summary>A recognizer that, once released, recognizes one final phrase.</summary>
    private sealed class ReleasedRecognizer(RecognizerRelease release) : SpeechRecognizer
    {
        public override string ProviderName => "Released";

        public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
            AudioFormat format,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await release.Released.WaitAsync(ct);
            yield return new SpeechRecognitionResult("hello from the startup window", 0.9f, true, TimeSpan.FromSeconds(1));
        }
    }
}
