using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Verbara.Platform.Api.Services.AgentAssist;
using Verbara.Platform.Api.Services.Speech;
using Verbara.Sdk.Audio;
using Verbara.Sdk.Pro.AgentAssist.Features;
using Verbara.Sdk.VoiceAi;

namespace Verbara.Platform.Api.Tests.AgentAssist;

/// <summary>
/// A feature store holding one state in memory that counts the session reads and can hold them until released, so a
/// test can start both streams of a session while the first read is still in flight.
/// </summary>
internal sealed class CountingFeatureStore(AgentAssistFeatureState state) : IAgentAssistFeatureStore
{
    private int _sessionReads;

    public AgentAssistFeatureState State { get; set; } = state;

    /// <summary>When set, each session read waits for it before answering.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Completed when a session read has started.</summary>
    public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int SessionReads => Volatile.Read(ref _sessionReads);

    public ValueTask<AgentAssistFeatureState> GetCurrentAsync(CancellationToken ct = default) => ValueTask.FromResult(State);

    public ValueTask<AgentAssistFeatureState> SetAsync(AgentAssistFeatureState desired, CancellationToken ct = default)
    {
        State = desired;
        return ValueTask.FromResult(desired);
    }

    public ValueTask<AgentAssistFeatureState> ReadAsync(CancellationToken ct = default) => ValueTask.FromResult(State);

    /// <summary>Answers the state stored when the read started, as a database read would.</summary>
    public async ValueTask<AgentAssistFeatureState> ReadForSessionAsync(CancellationToken ct = default)
    {
        var state = State;
        Interlocked.Increment(ref _sessionReads);
        ReadStarted.TrySetResult();
        if (Hold is { } hold)
            await hold.Task.WaitAsync(ct).ConfigureAwait(false);
        return state;
    }
}

/// <summary>What a <see cref="RecordingRecognizer"/> does with its stream.</summary>
public enum RecordedStreamEnd
{
    /// <summary>Reads every frame, then ends.</summary>
    Complete,

    /// <summary>Waits until its stream is cancelled.</summary>
    WaitForCancellation,

    /// <summary>Fails as a provider would, before yielding anything.</summary>
    Throw,
}

/// <summary>
/// Stands in for the shared recognizer factory: records the settings of each recognizer it builds and hands out
/// <see cref="RecordingRecognizer"/>s that record their disposal.
/// </summary>
internal sealed class RecordingRecognizerFactory(RecordedStreamEnd end = RecordedStreamEnd.Complete) : ISpeechRecognizerFactory
{
    private readonly ConcurrentQueue<RecordingRecognizer> _built = new();
    private readonly ConcurrentQueue<(SpeechRecognizerSettings Settings, string? HttpClientName)> _calls = new();
    private int _disposed;
    private int _started;
    private int _startedTarget = int.MaxValue;
    private readonly TaskCompletionSource _startedReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposedTarget = int.MaxValue;
    private readonly TaskCompletionSource _disposedReached = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<RecordingRecognizer> Built => [.. _built];

    public IReadOnlyList<(SpeechRecognizerSettings Settings, string? HttpClientName)> Calls => [.. _calls];

    public int Disposed => Volatile.Read(ref _disposed);

    /// <summary>Completes once <paramref name="count"/> built recognizers have been disposed.</summary>
    public Task DisposedAtLeast(int count)
    {
        _disposedTarget = count;
        if (Disposed >= count)
            _disposedReached.TrySetResult();
        return _disposedReached.Task;
    }

    /// <summary>Completes once <paramref name="count"/> built recognizers have started their stream.</summary>
    public Task StreamsStarted(int count)
    {
        _startedTarget = count;
        if (Volatile.Read(ref _started) >= count)
            _startedReached.TrySetResult();
        return _startedReached.Task;
    }

    public SpeechRecognizer Create(SpeechRecognizerSettings settings, string? httpClientName = null)
    {
        _calls.Enqueue((settings, httpClientName));
        var recognizer = new RecordingRecognizer(end, OnStarted, OnDisposed);
        _built.Enqueue(recognizer);
        return recognizer;
    }

    private void OnStarted()
    {
        if (Interlocked.Increment(ref _started) >= _startedTarget)
            _startedReached.TrySetResult();
    }

    private void OnDisposed()
    {
        if (Interlocked.Increment(ref _disposed) >= _disposedTarget)
            _disposedReached.TrySetResult();
    }
}

/// <summary>A recognizer that ends its stream as told and counts how often it is disposed.</summary>
internal sealed class RecordingRecognizer(RecordedStreamEnd end, Action onStarted, Action onDisposed) : SpeechRecognizer
{
    private int _disposeCalls;

    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    public int FramesRead { get; private set; }

    public override string ProviderName => "Recording";

    public override async IAsyncEnumerable<SpeechRecognitionResult> StreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> audioFrames,
        AudioFormat format,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        onStarted();
        switch (end)
        {
            case RecordedStreamEnd.Throw:
                throw new InvalidOperationException("the recording provider failed");
            case RecordedStreamEnd.WaitForCancellation:
                // Never completed: only the stream's cancellation ends the wait.
                await new TaskCompletionSource().Task.WaitAsync(ct).ConfigureAwait(false);
                break;
            default:
                await foreach (var _ in audioFrames.WithCancellation(ct).ConfigureAwait(false))
                    FramesRead++;
                break;
        }

        yield break;
    }

    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        onDisposed();
        return base.DisposeAsync();
    }
}

/// <summary>Counts <c>platform.agentassist.recognizer_unavailable</c> by <c>reason</c>, from one metrics instance only.</summary>
internal sealed class UnavailableCounts : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public UnavailableCounts(AgentAssistRecognizerMetrics metrics)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, metrics.Meter)
                && instrument.Name == AgentAssistRecognizerMetrics.RecognizerUnavailableInstrument)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "reason" && tag.Value is string reason)
                    _counts.AddOrUpdate(reason, value, (_, sum) => sum + value);
        });
        _listener.Start();
    }

    public long Get(string reason) => _counts.GetValueOrDefault(reason);

    public long Total => _counts.Values.Sum();

    public void Dispose() => _listener.Dispose();
}

/// <summary>
/// A logger provider that signals once a given number of <see cref="ToggleBackedSpeechRecognizer"/> release records
/// were written, so a test can wait for the session scope's teardown without polling.
/// </summary>
internal sealed class ReleasedRecognizerSignal : ILoggerProvider
{
    private readonly int _target;
    private int _count;

    public ReleasedRecognizerSignal(int target) => _target = target;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Count => Volatile.Read(ref _count);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void Dispose()
    {
    }

    private void Seen()
    {
        if (Interlocked.Increment(ref _count) >= _target)
            Reached.TrySetResult();
    }

    private sealed class Logger(string category, ReleasedRecognizerSignal owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == typeof(ToggleBackedSpeechRecognizer).FullName
                && formatter(state, exception).StartsWith("Released an Agent Assist stream recognizer", StringComparison.Ordinal))
                owner.Seen();
        }
    }
}
