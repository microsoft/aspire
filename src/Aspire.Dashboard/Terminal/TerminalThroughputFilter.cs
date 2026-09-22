// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Tokens;

namespace Aspire.Dashboard.Terminal;

/// <summary>
/// Samples token delivery to a dashboard terminal without retaining terminal content.
/// </summary>
internal sealed class TerminalThroughputFilter(ILogger logger, string connectionId, TimeProvider timeProvider)
    : IHex1bTerminalWorkloadFilter, IAsyncDisposable
{
    private readonly object _gate = new();
    private PeriodicTimer? _timer;
    private Task _reportTask = Task.CompletedTask;
    private long _lastSampleTimestamp;
    private long? _lastOutputTimestamp;
    private long _tokens;
    private long _inputTokens;
    private long _batches;
    private long _totalTokens;
    private int _maxBatchTokens;
    private TimeSpan _maxBatchGap;
    private bool _started;
    private bool _disposed;

    public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        _lastSampleTimestamp = timeProvider.GetTimestamp();
        _started = true;
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(5), timeProvider);
        _reportTask = ReportAsync(_timer);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnOutputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        // Hex1b calls this after tokenization and before applying tokens. Count only:
        // serializing or inspecting token contents here would perturb the workload.
        lock (_gate)
        {
            var now = timeProvider.GetTimestamp();
            if (_lastOutputTimestamp is { } previous)
            {
                var gap = timeProvider.GetElapsedTime(previous, now);
                if (gap > _maxBatchGap)
                {
                    _maxBatchGap = gap;
                }
            }
            _lastOutputTimestamp = now;
            _tokens += tokens.Count;
            _totalTokens += tokens.Count;
            _batches++;
            _maxBatchTokens = Math.Max(_maxBatchTokens, tokens.Count);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _inputTokens += tokens.Count;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    // The owner disposes the filter after the terminal, including failed construction
    // and disconnect paths, so logging never blocks Hex1b's output or teardown callbacks.
    public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;

    private async Task ReportAsync(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            WriteSample(isFinal: false);
        }
    }

    private void WriteSample(bool isFinal)
    {
        long tokens, inputTokens, batches, totalTokens;
        int maxBatchTokens;
        double seconds, maxBatchGapMs;
        double? lastOutputAgeMs;
        lock (_gate)
        {
            var now = timeProvider.GetTimestamp();
            seconds = timeProvider.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
            if (seconds <= 0)
            {
                return;
            }
            tokens = _tokens;
            inputTokens = _inputTokens;
            batches = _batches;
            totalTokens = _totalTokens;
            maxBatchTokens = _maxBatchTokens;
            maxBatchGapMs = _maxBatchGap.TotalMilliseconds;
            lastOutputAgeMs = _lastOutputTimestamp is { } previous
                ? timeProvider.GetElapsedTime(previous, now).TotalMilliseconds : null;
            _lastSampleTimestamp = now;
            _tokens = _inputTokens = _batches = 0;
            _maxBatchTokens = 0;
            _maxBatchGap = TimeSpan.Zero;
        }

        // Logging stays outside both the counter lock and the terminal output pump.
        // Gaps include idle time, parsing, scheduling and processing; they are not CPU timings.
        logger.LogInformation(
            "Terminal token throughput ({ConnectionId}): {Tokens} tokens in {Seconds:F3}s ({TokensPerSecond:F1} tokens/s), " +
            "{Batches} batches ({BatchesPerSecond:F1} batches/s), max batch {MaxBatchTokens} tokens, " +
            "max batch gap {MaxBatchGapMs:F3}ms, last output {LastOutputAgeMs:F3}ms ago, " +
            "{InputTokens} input tokens, {TotalTokens} total output tokens, final={IsFinal}.",
            connectionId, tokens, seconds, tokens / seconds, batches, batches / seconds,
            maxBatchTokens, maxBatchGapMs, lastOutputAgeMs, inputTokens, totalTokens, isFinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _timer?.Dispose();
        await _reportTask.ConfigureAwait(false);
        if (_started)
        {
            WriteSample(isFinal: true);
        }
    }
}
