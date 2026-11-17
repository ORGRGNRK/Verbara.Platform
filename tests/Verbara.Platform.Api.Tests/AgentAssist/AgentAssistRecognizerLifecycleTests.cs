#pragma warning disable CA2012 // Use ValueTasks correctly: NSubstitute setup chains .Returns() on ValueTask

using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Verbara.Platform.Api.DependencyInjection;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Platform.Api.Tests.Logging;
using Verbara.Platform.Core.Configuration;
using Verbara.Sdk;
using Verbara.Sdk.Ami.Connection;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Pro.AgentAssist.DependencyInjection;
using Verbara.Sdk.Pro.AgentAssist.Engine;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.Pro.Licensing;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Manager;
using Verbara.Sdk.VoiceAi;
using Verbara.Sdk.VoiceAi.AudioSocket;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.2 (design D5, Pro decision P7): the recognizers of a session the real Pro engine
/// runs, through the Api's Agent Assist recognizer registrations (<see cref="AgentAssistRecognizerExtensions"/>) and
/// <c>WithSpeechRecognizer&lt;ToggleBackedSpeechRecognizer&gt;()</c>, in a container built with scope validation on.
/// The engine resolves both recognizers of a session from one scope and disposes it at teardown; no test disposes a
/// recognizer by hand. The only stand-in is the factory, a recording one, so each inner recognizer's disposal is seen.
/// </summary>
public sealed class AgentAssistRecognizerLifecycleTests
{
    private const string ServerId = "server-1";
    private const string AudioSocketUuidVariable = "AUDIOSOCKET_UUID";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Theory]
    [InlineData(RecordedStreamEnd.Complete)]
    [InlineData(RecordedStreamEnd.WaitForCancellation)]
    [InlineData(RecordedStreamEnd.Throw)]
    public async Task Session_ShouldDisposeEachInnerRecognizerAndBothWrappers_WhenItsStreamsCompleteAreCancelledOrThrow(
        RecordedStreamEnd end)
    {
        var factory = new RecordingRecognizerFactory(end);
        var released = new ReleasedRecognizerSignal(target: 2);
        var call = new CallSession("aa-b2-" + end, "1712345678.40", ServerId, CallDirection.Inbound);
        await using var engine = await EngineWithToggleBackedRecognizer.StartAsync(call, factory, released);

        await factory.StreamsStarted(2).WaitAsync(Bound);
        if (end == RecordedStreamEnd.Complete)
            await engine.SendAudioThenHangUpAsync();   // Asterisk ends both snoops' audio: each stream completes
        if (end != RecordedStreamEnd.WaitForCancellation)
        {
            await factory.DisposedAtLeast(2).WaitAsync(Bound);
            factory.Built.Should().AllSatisfy(r => r.DisposeCalls.Should().Be(1),
                "each inner recognizer is disposed when its stream ends, before the session does");
            released.Count.Should().Be(0, "the wrappers live until the session's teardown");
        }

        await engine.EndCallAsync(call);
        await released.Reached.Task.WaitAsync(Bound);
        await factory.DisposedAtLeast(2).WaitAsync(Bound);

        factory.Built.Should().HaveCount(2, "the session's two streams each built one inner recognizer")
            .And.AllSatisfy(r => r.DisposeCalls.Should().Be(1));
        released.Count.Should().Be(2, "disposing the session scope disposes both wrappers");
    }

    [Fact]
    public async Task Engine_ShouldDriveOneSessionThroughTheProScopeWithoutAnError_WhenScopesAreValidated()
    {
        var factory = new RecordingRecognizerFactory(RecordedStreamEnd.WaitForCancellation);
        var released = new ReleasedRecognizerSignal(target: 2);
        var call = new CallSession("aa-b2-scoped", "1712345678.41", ServerId, CallDirection.Outbound);
        await using var engine = await EngineWithToggleBackedRecognizer.StartAsync(call, factory, released);

        await factory.StreamsStarted(2).WaitAsync(Bound);
        factory.Calls.Should().HaveCount(2).And.AllSatisfy(c => c.HttpClientName.Should().Be("agentassist-stt"));
        await engine.EndCallAsync(call);
        await released.Reached.Task.WaitAsync(Bound);

        engine.Store.SessionReads.Should().Be(1, "the session's two recognizers share one scoped read");
        engine.Logs.Records.Where(r => r.Level >= LogLevel.Error).Should().BeEmpty();
        engine.Services.Should().NotContain(d => d.ServiceType == typeof(Func<SpeechRecognizer>),
            "no recognizer is ever resolved from the root (Pro P7 retires the singleton factory)");
    }

    [Fact]
    public async Task SpeechRecognizer_ShouldFailFromTheRootForTheScopedSessionOnlyAndResolveInAScope_WhenScopesAreValidated()
    {
        var services = EngineWithToggleBackedRecognizer.BuildServices(
            new CountingFeatureStore(AgentAssistFeatureState.Disabled), new RecordingRecognizerFactory(), new ReleasedRecognizerSignal(1),
            new LogRecordCapture(), Substitute.For<ICallSessionManager>(),
            new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance),
            new VerbaraServerPool(Substitute.For<IAmiConnectionFactory>(), NullLoggerFactory.Instance));
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var fromRoot = () => provider.GetRequiredService<SpeechRecognizer>();

        fromRoot.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(nameof(AgentAssistRecognizerSession),
                "the root resolution fails because the recognizer needs the session's scoped object, and for no other reason");
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SpeechRecognizer>().Should().BeOfType<ToggleBackedSpeechRecognizer>();
        services.Should().NotContain(d => d.ServiceType == typeof(Func<SpeechRecognizer>));
    }

    /// <summary>
    /// The Pro engine in a container validated on build, with the Api's recognizer registrations, a recording factory and
    /// one session registered for a given call. The ARI client and Asterisk's dialplan are stood in for as in
    /// <c>AgentAssistLiveSessionsRegisteredSessionTests</c>.
    /// </summary>
    private sealed class EngineWithToggleBackedRecognizer : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IReadOnlyList<IHostedService> _hosted;
        private readonly ConcurrentBag<AudioSocketClient> _clients;
        private readonly SubscribedSignal _events;

        private EngineWithToggleBackedRecognizer(
            ServiceProvider provider, IServiceCollection services, IReadOnlyList<IHostedService> hosted,
            ConcurrentBag<AudioSocketClient> clients, SubscribedSignal events, CountingFeatureStore store, LogRecordCapture logs)
        {
            _provider = provider;
            _hosted = hosted;
            _clients = clients;
            _events = events;
            Services = services;
            Store = store;
            Logs = logs;
            Supervisor = provider.GetRequiredService<AgentAssistSupervisor>();
        }

        public IServiceCollection Services { get; }

        public CountingFeatureStore Store { get; }

        public LogRecordCapture Logs { get; }

        public AgentAssistSupervisor Supervisor { get; }

        public static ServiceCollection BuildServices(
            CountingFeatureStore store, ISpeechRecognizerFactory factory, ReleasedRecognizerSignal released,
            LogRecordCapture logs, ICallSessionManager sessions, AudioSocketServer audio, VerbaraServerPool pool)
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug).AddProvider(logs).AddProvider(released));
            services.AddMetrics();
            services.AddSingleton(sessions);
            services.AddSingleton(pool);
            services.AddSingleton(audio);                                  // the engine's TryAdd keeps this one
            services.AddSingleton<ILicenseGuard>(new AllowEverything());   // AddProLicenseGuard's TryAdd keeps this one
            services.Configure<AgentAssistOptions>(o => o.Deepgram.Language = "es");
            services.AddSingleton(new AgentAssistCredentialsProtector(new EphemeralDataProtectionProvider()));
            services.AddSingleton<IAgentAssistFeatureStore>(store);
            services.AddSingleton(factory);                                // AddAgentAssistRecognizer's TryAdd keeps this one

            services.AddAgentAssistRecognizer();
            // Without an advertised address the engine creates no snoop; the fake dialplan ignores its value.
            services.AddProAgentAssist(b => b
                .WithSnoop(o => o.AudioSocketAdvertisedAddress = "127.0.0.1:9093")
                .WithSpeechRecognizer<ToggleBackedSpeechRecognizer>());
            return services;
        }

        public static async Task<EngineWithToggleBackedRecognizer> StartAsync(
            CallSession call, RecordingRecognizerFactory factory, ReleasedRecognizerSignal released)
        {
            using var guard = new CancellationTokenSource(Bound);
            var events = new SubscribedSignal();
            var sessions = Substitute.For<ICallSessionManager>();
            sessions.Events.Returns(events);
            sessions.GetById(call.SessionId).Returns(call);

            var audio = new AudioSocketServer(new AudioSocketOptions { Port = 0 }, NullLogger<AudioSocketServer>.Instance);
            var clients = new ConcurrentBag<AudioSocketClient>();
            var store = new CountingFeatureStore(AgentAssistFeatureState.Disabled);
            var logs = new LogRecordCapture();
            var services = BuildServices(store, factory, released, logs, sessions, audio, PoolWithAri(audio, clients));

            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            var protector = provider.GetRequiredService<AgentAssistCredentialsProtector>();
            store.State = new AgentAssistFeatureState(
                true, "deepgram", protector.Protect(new Dictionary<string, string> { ["apiKey"] = "dg-lifecycle-key" }),
                DateTimeOffset.UtcNow, "admin");

            var hosted = provider.GetServices<IHostedService>().ToList();
            foreach (var service in hosted)
                await service.StartAsync(guard.Token);

            // The engine subscribes after its AudioSocket server is listening.
            await events.Subscribed.WaitAsync(guard.Token);

            var engine = new EngineWithToggleBackedRecognizer(provider, services, hosted, clients, events, store, logs);
            var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (engine.Supervisor.SessionStarted.Subscribe(s =>
                   {
                       if (s.SessionId == call.SessionId)
                           registered.TrySetResult();
                   }))
            {
                events.OnNext(new CallConnectedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, "agent-1", "support", TimeSpan.Zero));
                await registered.Task.WaitAsync(guard.Token);
            }

            return engine;
        }

        /// <summary>Hangs the call up, as the session manager reports it; the engine tears the session down.</summary>
        public async Task EndCallAsync(CallSession call)
        {
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = Supervisor.SessionEnded.Subscribe(s =>
            {
                if (s.SessionId == call.SessionId)
                    ended.TrySetResult();
            });
            _events.OnNext(new CallEndedEvent(call.SessionId, ServerId, DateTimeOffset.UtcNow, null, TimeSpan.FromSeconds(1), null));
            await ended.Task.WaitAsync(Bound);
        }

        /// <summary>Each snoop sends one frame of audio, then Asterisk's hang-up frame, which ends its audio stream.</summary>
        public async Task SendAudioThenHangUpAsync()
        {
            foreach (var client in _clients)
            {
                await client.SendAudioAsync(new byte[320]);
                await client.SendHangupAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var service in _hosted)
                await service.StopAsync(CancellationToken.None);
            foreach (var client in _clients)
                await client.DisposeAsync();
            await _provider.DisposeAsync();
            _events.Dispose();
        }

        /// <summary>
        /// A node whose ARI client answers the snoop requests and plays Asterisk's dialplan: continuing a snoop
        /// connects one AudioSocket client to <paramref name="audio"/> with the id the engine set on it.
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
    }

    /// <summary>The session-event stream, with a signal for the moment the engine subscribes to it.</summary>
    private sealed class SubscribedSignal : IObservable<SessionDomainEvent>, IDisposable
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
