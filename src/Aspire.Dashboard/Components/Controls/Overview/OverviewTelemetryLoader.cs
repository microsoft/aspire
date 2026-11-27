// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Loads the telemetry summarized by an overview's telemetry cards, and reloads it when the source sends new
/// telemetry. Members must be called on the owning component's render dispatcher.
/// </summary>
internal sealed class OverviewTelemetryLoader : IDisposable
{
    // Telemetry can arrive many times per second. Coalesce updates so the overview queries the repository at most
    // a couple of times per second.
    private static readonly TimeSpan s_refreshDelay = TimeSpan.FromMilliseconds(500);

    private readonly ITelemetryRepository _repository;
    private readonly ILogger _logger;
    private readonly Func<Action, Task> _invokeAsync;
    private readonly Action _onLoaded;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private CancellationTokenSource? _cts;
    private Subscription? _logsSubscription;
    private Subscription? _tracesSubscription;
    private Subscription? _metricsSubscription;
    // Replaced when the key changes, so a canceled refresh that hasn't observed its cancellation yet can't make the
    // next source's first refresh look like it's already pending.
    private RefreshGeneration _refresh = new();

    /// <param name="repository">The repository the telemetry is read from.</param>
    /// <param name="logger">Logs failed reads.</param>
    /// <param name="invokeAsync">Runs work on the owning component's render dispatcher.</param>
    /// <param name="onLoaded">Called on the render dispatcher after new telemetry is loaded.</param>
    /// <param name="delayAsync">Waits between a telemetry notification and the read that follows it.</param>
    public OverviewTelemetryLoader(ITelemetryRepository repository, ILogger logger, Func<Action, Task> invokeAsync, Action onLoaded, Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _repository = repository;
        _logger = logger;
        _invokeAsync = invokeAsync;
        _onLoaded = onLoaded;
        _delayAsync = delayAsync;
    }

    public ResourceKey? Key { get; private set; }

    public OverviewTelemetry? Telemetry { get; private set; }

    /// <summary>
    /// Gets whether the first read for the current key completed. Telemetry cards show a loading skeleton until then.
    /// </summary>
    public bool IsLoaded { get; private set; }

    internal Task RefreshTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Subscribes to the telemetry of <paramref name="key"/> and starts loading it, replacing the previous source's
    /// subscriptions and telemetry. Does nothing when the key is unchanged.
    /// </summary>
    /// <returns><see langword="true"/> when the key changed.</returns>
    public bool SetKey(ResourceKey? key)
    {
        if (Equals(Key, key))
        {
            return false;
        }

        Unsubscribe();
        Key = key;
        // Cleared so the telemetry cards show their loading skeleton again instead of flashing the previous source's
        // stale data or popping in abruptly once the new data arrives.
        Telemetry = null;
        IsLoaded = false;
        _refresh = new();

        if (key is { } k)
        {
            _cts = new CancellationTokenSource();
            _logsSubscription = _repository.OnNewLogs(k, SubscriptionType.Other, OnTelemetryChangedAsync);
            _tracesSubscription = _repository.OnNewTraces(k, SubscriptionType.Other, OnTelemetryChangedAsync);
            _metricsSubscription = _repository.OnNewMetrics(k, SubscriptionType.Other, OnTelemetryChangedAsync);
            QueueRefresh(immediate: true);
        }

        return true;
    }

    /// <summary>
    /// Reloads the telemetry after a short delay, or immediately. A request made while a read is running is
    /// coalesced into one more read after it.
    /// </summary>
    public void QueueRefresh(bool immediate = false)
    {
        var refresh = _refresh;
        if (Key is not { } key || _cts is not { } cts)
        {
            return;
        }

        // Keep the gate on the render dispatcher through publication so a slower read cannot overwrite a newer one.
        if (refresh.IsRunning)
        {
            refresh.RefreshRequested = true;
            return;
        }

        refresh.IsRunning = true;
        RefreshTask = RefreshAsync(refresh, key, immediate, cts.Token);
    }

    private Task OnTelemetryChangedAsync() => _invokeAsync(() => QueueRefresh());

    private async Task RefreshAsync(RefreshGeneration refresh, ResourceKey key, bool immediate, CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                refresh.RefreshRequested = false;
                try
                {
                    if (!immediate)
                    {
                        await _delayAsync(s_refreshDelay, cancellationToken);
                    }
                    immediate = false;
                    if (!IsCurrentRefresh())
                    {
                        return;
                    }

                    // This read covers notifications that arrived during the delay.
                    refresh.RefreshRequested = false;
                    var telemetry = await Task.Run(() => OverviewTelemetry.LoadAsync(_repository, key, cancellationToken), cancellationToken);
                    if (!IsCurrentRefresh())
                    {
                        return;
                    }

                    Telemetry = telemetry;
                    IsLoaded = true;
                    _onLoaded();
                }
                catch (OperationCanceledException) when (!IsCurrentRefresh())
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error loading telemetry summary for the overview.");
                }
            }
            while (refresh.RefreshRequested && IsCurrentRefresh());
        }
        finally
        {
            refresh.IsRunning = false;
        }

        bool IsCurrentRefresh() => ReferenceEquals(_refresh, refresh) && !cancellationToken.IsCancellationRequested;
    }

    private void Unsubscribe()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _logsSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        _logsSubscription = _tracesSubscription = _metricsSubscription = null;
    }

    public void Dispose()
    {
        Key = null;
        Unsubscribe();
    }

    private sealed class RefreshGeneration
    {
        public bool IsRunning;
        public bool RefreshRequested;
    }
}
