// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

public sealed class ResourceTerminalsAvailabilityProvider : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private Task? _watchTask;
    private bool _available;
    private bool _disposed;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ILogger<ResourceTerminalsAvailabilityProvider> Logger { get; init; }

    [Parameter, EditorRequired]
    public required EventCallback<bool> AvailabilityChanged { get; set; }

    protected override void OnInitialized()
    {
        if (DashboardClient.IsEnabled && !DashboardClient.IsReadOnly)
        {
            _watchTask = WatchAsync(_cts.Token);
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DashboardClient.WhenConnected.WaitAsync(cancellationToken);
            var (snapshot, updates) = await DashboardClient.SubscribeResourcesAsync(cancellationToken);
            await InvokeAsync(async () =>
            {
                foreach (var resource in snapshot)
                {
                    _resources[resource.Name] = resource;
                }
                await ReportAvailabilityAsync(cancellationToken);
            });

            await foreach (var changes in updates.WithCancellation(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resources.Remove(resource.Name);
                        }
                        else
                        {
                            _resources[resource.Name] = resource;
                        }
                    }
                    await ReportAvailabilityAsync(cancellationToken);
                });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to watch resource terminal availability.");
            await DispatchExceptionAsync(ex);
        }
    }

    private async Task ReportAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var available = _resources.Values.Any(ResourceSelectHelpers.HasUsableTerminal);
        if (_available != available)
        {
            _available = available;
            await AvailabilityChanged.InvokeAsync(available);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await _cts.CancelAsync();
        if (_watchTask is not null)
        {
            await _watchTask;
        }
        _cts.Dispose();
    }
}
