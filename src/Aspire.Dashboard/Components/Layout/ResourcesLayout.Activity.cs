// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Frozen;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

public sealed partial class ResourcesLayout
{
    private const int MaxPaneActivitySpans = 5000;
    private static readonly TimeSpan s_paneActivityWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan s_paneActivityRefreshDelay = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan s_paneActivityRefreshInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan s_paneActivityQueryTimeout = TimeSpan.FromSeconds(5);
    private Subscription? _paneActivitySubscription;
    private Task? _paneActivityRefreshTask;
    private Task? _paneActivityTimerTask;
    private CancellationTokenSource? _paneActivityQueryCancellation;
    private bool _paneActivityVisible = true;
    private int _paneActivityRefreshPending;
    private int _paneActivityGeneration;
    private FrozenDictionary<ResourceKey, ResourcePaneActivitySnapshot> _paneActivity = FrozenDictionary<ResourceKey, ResourcePaneActivitySnapshot>.Empty;
    private DateTime _paneActivityWindowEnd;
    private bool _paneActivitySampled;
    internal event Action? PaneActivityChanged;
    internal Task PaneActivityRefreshTask => _paneActivityRefreshTask ?? Task.CompletedTask;

    internal ResourcePaneActivitySnapshot? GetPaneActivitySnapshot(ResourceKey key) =>
        _paneActivity.GetValueOrDefault(key);

    private void QueuePaneActivityRefresh(bool immediate = false)
    {
        if (_isDisposed || !_isPaneFullCanvas || !_paneActivityVisible || Interlocked.Exchange(ref _paneActivityRefreshPending, 1) == 1)
        {
            return;
        }

        _paneActivityRefreshTask = RefreshPaneActivityAsync(_paneActivityGeneration, immediate, _cts.Token);
    }

    private void ClearPaneActivity()
    {
        _paneActivityGeneration++;
        _paneActivityQueryCancellation?.Cancel();
        _paneActivity = FrozenDictionary<ResourceKey, ResourcePaneActivitySnapshot>.Empty;
        PaneActivityChanged?.Invoke();
    }

    [JSInvokable]
    public void SetPaneActivityVisible(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _paneActivityVisible = visible;
        if (visible)
        {
            QueuePaneActivityRefresh(immediate: true);
        }
        else
        {
            ClearPaneActivity();
        }
    }

    private async Task RefreshPaneActivityPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_paneActivityRefreshInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(() => QueuePaneActivityRefresh(immediate: true)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshPaneActivityAsync(int generation, bool immediate, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(s_paneActivityQueryTimeout, TimeProvider);
        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        _paneActivityQueryCancellation = queryCancellation;
        try
        {
            if (!immediate)
            {
                await Task.Delay(s_paneActivityRefreshDelay, TimeProvider, queryCancellation.Token).ConfigureAwait(false);
            }
            if (!_isPaneFullCanvas || !_paneActivityVisible || generation != _paneActivityGeneration || queryCancellation.IsCancellationRequested)
            {
                return;
            }

            var end = TimeProvider.GetUtcNow().UtcDateTime;
            var start = end - s_paneActivityWindow;
            var keys = TelemetryRepository.GetResources().Select(resource => resource.ResourceKey).Distinct().ToList();
            // An empty repository request means ALL resources. There is nothing to summarize yet.
            var response = keys.Count == 0
                ? new GetRequestSpansResponse { Items = [], TotalItemCount = 0 }
                : await TelemetryRepository.GetRequestSpansAsync(new GetRequestSpansRequest
                {
                    ResourceKeys = keys,
                    StartTime = start,
                    EndTime = end,
                    MaxCount = MaxPaneActivitySpans
                }, queryCancellation.Token).ConfigureAwait(false);
            var activity = ResourcePaneActivity.Summarize(response);
            var refreshed = TimeProvider.GetUtcNow().UtcDateTime;
            await InvokeAsync(() =>
            {
                if (_isDisposed || !_isPaneFullCanvas || !_paneActivityVisible || generation != _paneActivityGeneration)
                {
                    return;
                }

                var sampled = response.TotalItemCount > response.Items.Count;
                var changed = sampled != _paneActivitySampled || activity.Count != _paneActivity.Count ||
                    activity.Any(pair => !_paneActivity.TryGetValue(pair.Key, out var previous) || pair.Value != previous.Activity);
                // Unrelated trace arrivals must not repaint the list. A visible snapshot still
                // renews its exact URL window at the bounded cadence, even if counts are stable.
                if (!changed && (activity.Count == 0 || end - _paneActivityWindowEnd < s_paneActivityRefreshInterval))
                {
                    return;
                }

                var renewWindow = end - _paneActivityWindowEnd >= s_paneActivityRefreshInterval;
                _paneActivity = activity.ToFrozenDictionary(pair => pair.Key,
                    pair => !renewWindow && _paneActivity.TryGetValue(pair.Key, out var previous) &&
                        previous.Activity == pair.Value && previous.Sampled == sampled
                            ? previous
                            : new ResourcePaneActivitySnapshot(pair.Value, start, end, refreshed, sampled));
                if (renewWindow)
                {
                    _paneActivityWindowEnd = end;
                }
                _paneActivitySampled = sampled;
                // Telemetry-only updates must not recreate every row's event handlers.
                // Duplicate tag memberships share the same immutable snapshot.
                PaneActivityChanged?.Invoke();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (queryCancellation.IsCancellationRequested)
        {
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning("Resource pane activity query exceeded its time limit.");
                await InvokeAsync(() =>
                {
                    if (!_isDisposed && generation == _paneActivityGeneration && _paneActivity.Count > 0)
                    {
                        _paneActivity = FrozenDictionary<ResourceKey, ResourcePaneActivitySnapshot>.Empty;
                        PaneActivityChanged?.Invoke();
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error loading resource pane activity.");
            await InvokeAsync(() =>
            {
                if (!_isDisposed && generation == _paneActivityGeneration)
                {
                    _paneActivity = FrozenDictionary<ResourceKey, ResourcePaneActivitySnapshot>.Empty;
                    PaneActivityChanged?.Invoke();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_paneActivityQueryCancellation, queryCancellation))
            {
                _paneActivityQueryCancellation = null;
            }
            Interlocked.Exchange(ref _paneActivityRefreshPending, 0);
            if (!_isDisposed && !cancellationToken.IsCancellationRequested && generation != _paneActivityGeneration)
            {
                await InvokeAsync(() => QueuePaneActivityRefresh(immediate: true)).ConfigureAwait(false);
            }
        }
    }
}
