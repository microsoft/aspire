// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Dashboard.Utils;

[DebuggerDisplay("Name = {Name}")]
public sealed class CallbackThrottler
{
    // This interval balances responsiveness with the performance cost of repeatedly executing callbacks.
    public static readonly TimeSpan DefaultMinExecuteInterval = TimeSpan.FromMilliseconds(500);

    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
    private DateTime? _lastExecute;

    public CallbackThrottler(string name, ILogger logger, TimeSpan minExecuteInterval, Func<Task> callback, ExecutionContext? executionContext)
    {
        Name = name;
        _logger = logger;
        _minExecuteInterval = minExecuteInterval;
        _callback = callback;
        _executionContext = executionContext;
        _cts = new CancellationTokenSource();
        _cancellationToken = _cts.Token;
    }

    public string Name { get; }

    private readonly ILogger _logger;
    private readonly TimeSpan _minExecuteInterval;
    private readonly Func<Task> _callback;
    private readonly ExecutionContext? _executionContext;
    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _cancellationToken;

    private async Task<bool> TryQueueAsync(CancellationToken cancellationToken)
    {
        // Immediately exit if the lock isn't available.
        // Either the throttler is already executing or its been disposed.
        if (!TryAquireLock(cancellationToken))
        {
            return false;
        }

        try
        {
            var lastExecute = _lastExecute;
            if (lastExecute != null)
            {
                var minExecuteInterval = _minExecuteInterval;
                var s = lastExecute.Value.Add(minExecuteInterval) - DateTime.UtcNow;
                if (s > TimeSpan.Zero)
                {
                    _logger.LogTrace("Callback '{Name}' minimum execute interval of {MinExecuteInterval} hit. Waiting {DelayInterval}.", Name, minExecuteInterval, s);
                    await Task.Delay(s, cancellationToken).ConfigureAwait(false);
                }
            }

            _lastExecute = DateTime.UtcNow;
            return true;
        }
        finally
        {
            _lock.Release();
        }

        bool TryAquireLock(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogTrace("Callback '{Name}' has been disposed.", Name);
                return false;
            }

            try
            {
                var success = _lock.Wait(0, cancellationToken);
                if (!success)
                {
                    _logger.LogTrace("Callback '{Name}' update already queued.", Name);
                    return false;
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogTrace("Callback '{Name}' has been disposed.", Name);
                return false;
            }

            return true;
        }
    }

    public async Task ExecuteAsync()
    {
        // Try to queue the subscription callback.
        // If another caller is already in the queue then exit without calling the callback.
        if (!await TryQueueAsync(_cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Set the execution context to the one captured when the subscription was created.
            // This ensures that the callback runs in the same context as the subscription was created.
            // For example, the request culture is used to format content in the callback.
            //
            // No need to restore back to the original context because the callback is running on
            // a background task. The task finishes immediately after the callback.
            if (_executionContext != null)
            {
                ExecutionContext.Restore(_executionContext);
            }

            _logger.LogTrace("Callback '{Name}' executing.", Name);
            await _callback().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in callback.");
        }
    }

    public void Execute()
    {
        // Execute on a background thread.
        // The caller doesn't want to wait while the execution is running or receive exceptions.
        _ = Task.Run(ExecuteAsync);
    }

    public void Dispose()
    {
        _cts.Cancel();
        // It's safe to not dispose SemaphoreSlim because we're not using AvailableWaitHandle.
    }
}
