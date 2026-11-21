using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Verbara.Sdk;
using Verbara.Sdk.Enums;
using Verbara.Sdk.Live.Server;
using Verbara.Sdk.Sessions;
using Verbara.Sdk.Sessions.Extensions;
using Verbara.Sdk.Sessions.Manager;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// agent-assist-works-end-to-end task B.5 (design D8): Agent Assist taps every call it serves with two snoop channels,
/// and the session manager of the Sdk this build pins must not treat a snoop as a call. The manager here is the pinned
/// Sdk's real <see cref="CallSessionManager"/>, attached to a real <see cref="VerbaraServer"/> whose channel table is fed
/// directly, so the test reads what the shipped package does, not a stand-in.
/// </summary>
/// <remarks>
/// The snoop carries the shape Asterisk gives a real ARI snoop: the name <c>Snoop/&lt;target uniqueid&gt;-&lt;n&gt;</c> and
/// a linkedid that is its own uniqueid. The channel manager raises its events inline and the session manager acts inside
/// them, so every reading below is taken after the step that caused it has fully run.
/// </remarks>
[SuppressMessage("Reliability", "CA1001:Types that own disposable fields should be disposable", Justification = "Disposed through IAsyncLifetime.DisposeAsync")]
public sealed class SnoopChannelSessionTests : IAsyncLifetime
{
    private const string ServerId = "primary";

    private readonly IAmiConnection _connection = Substitute.For<IAmiConnection>();
    private readonly VerbaraServer _server;
    private readonly CallSessionManager _manager;
    private readonly ConcurrentQueue<SessionDomainEvent> _events = new();
    private readonly IDisposable _subscription;

    public SnoopChannelSessionTests()
    {
        _connection.AsteriskVersion.Returns("22.0.0");
        _server = new VerbaraServer(_connection, NullLogger<VerbaraServer>.Instance);
        _manager = new CallSessionManager(
            Options.Create(new SessionOptions()), NullLogger<CallSessionManager>.Instance, new DictionarySessionStore());
        _subscription = _manager.Events.Subscribe(_events.Enqueue);
        _manager.AttachToServer(_server, ServerId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _subscription.Dispose();
        await _manager.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Theory]
    [InlineData("Snoop/1794794992.7-00000001", "1794794993.11")]
    [InlineData("Snoop/1794794992.7-00000002", "1794794993.12")]
    public void Newchannel_ShouldOpenNoSessionAndRaiseNoCallStarted_WhenTheChannelIsASnoop(string name, string uniqueId)
    {
        _server.Channels.OnNewChannel(uniqueId, name, ChannelState.Up, linkedId: uniqueId);
        var whileUp = Read(uniqueId);
        _server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);
        var afterHangup = Read(uniqueId);

        new { WhileUp = whileUp, AfterHangup = afterHangup }.Should().BeEquivalentTo(
            new { WhileUp = Reading.Nothing, AfterHangup = Reading.Nothing },
            $"'{name}' is a tap on a call, not a call: it opens no session and raises no call event while it is up or "
            + "after it hangs up");
    }

    /// <summary>Positive control: the same arrival and hangup on an ordinary channel opens one call and ends it once.</summary>
    [Fact]
    public void Newchannel_ShouldOpenOneSessionAndRaiseOneCallStarted_WhenTheChannelIsAnOrdinaryLeg()
    {
        const string uniqueId = "1794794993.13";

        _server.Channels.OnNewChannel(uniqueId, "PJSIP/trunk-00000001", ChannelState.Up, linkedId: uniqueId);
        var whileUp = Read(uniqueId);
        _server.Channels.OnHangup(uniqueId, HangupCause.NormalClearing);
        var afterHangup = Read(uniqueId);

        new { WhileUp = whileUp, AfterHangup = afterHangup }.Should().BeEquivalentTo(
            new
            {
                WhileUp = new Reading(FoundByLinkedId: true, Active: 1, Started: 1, Ended: 0),
                AfterHangup = new Reading(FoundByLinkedId: true, Active: 0, Started: 1, Ended: 1),
            },
            "an ordinary leg is a call, started once and ended once: this is what the snoop cases would read if the "
            + "pinned session manager treated a snoop as a call");
    }

    private Reading Read(string linkedId) => new(
        FoundByLinkedId: _manager.GetByLinkedId(linkedId) is not null,
        Active: _manager.ActiveSessions.Count(),
        Started: _events.OfType<CallStartedEvent>().Count(),
        Ended: _events.OfType<CallEndedEvent>().Count());

    private sealed record Reading(bool FoundByLinkedId, int Active, int Started, int Ended)
    {
        public static readonly Reading Nothing = new(FoundByLinkedId: false, Active: 0, Started: 0, Ended: 0);
    }

    /// <summary>The smallest session store the manager accepts: the test reads the manager, never the store.</summary>
    private sealed class DictionarySessionStore : SessionStoreBase
    {
        private readonly ConcurrentDictionary<string, CallSession> _sessions = new();

        public override ValueTask SaveAsync(CallSession session, CancellationToken ct)
        {
            _sessions[session.SessionId] = session;
            return ValueTask.CompletedTask;
        }

        public override ValueTask<CallSession?> GetAsync(string sessionId, CancellationToken ct) =>
            ValueTask.FromResult(_sessions.GetValueOrDefault(sessionId));
    }
}
