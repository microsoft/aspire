// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// Watches the live run's resources and telemetry to report which optional navigation entries have something to
/// show: terminals, parameters and telemetry sources. It's rendered by the main layout because the navigation is
/// shown on every page, not only those that already watch resources.
/// </summary>
public sealed class NavigationAvailabilityProvider : ComponentBase, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private Subscription? _telemetryResourcesSubscription;
    private Task? _watchTask;
    private NavigationAvailability? _reported;
    private bool _resourcesLoaded;
    private bool _disposed;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    // The live run's telemetry. The provider is only rendered for the live run (see MainLayout).
    [Inject]
    public required ITelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required ILogger<NavigationAvailabilityProvider> Logger { get; init; }

    [Parameter, EditorRequired]
    public required EventCallback<NavigationAvailability> AvailabilityChanged { get; set; }

    protected override void OnInitialized()
    {
        if (DashboardClient.IsEnabled && !DashboardClient.IsReadOnly)
        {
            _telemetryResourcesSubscription = TelemetryRepository.OnNewResources(() => InvokeAsync(() => ReportAvailabilityAsync(_cts.Token)));
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
                _resourcesLoaded = true;
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
            Logger.LogError(ex, "Failed to watch navigation availability.");
            await DispatchExceptionAsync(ex);
        }
    }

    private async Task ReportAvailabilityAsync(CancellationToken cancellationToken)
    {
        // Until the app model is loaded, every telemetry resource would look like a telemetry source and there would
        // be no parameters. Keep the entries as they are rather than disabling them for a moment on every page load.
        if (cancellationToken.IsCancellationRequested || !_resourcesLoaded)
        {
            return;
        }

        var availability = new NavigationAvailability(
            HasResourceTerminals: _resources.Values.Any(ResourceSelectHelpers.HasUsableTerminal),
            HasParameters: _resources.Values.Any(r => r.IsParameter),
            HasTelemetrySources: HasTelemetrySources());
        if (_reported != availability)
        {
            _reported = availability;
            await AvailabilityChanged.InvokeAsync(availability);
        }
    }

    // Telemetry sources are the resources that send telemetry without being part of the app model (for example a
    // browser app), the same ones the telemetry pane lists.
    private bool HasTelemetrySources()
    {
        var telemetryResources = TelemetryRepository.GetResources();
        if (telemetryResources.Count == 0)
        {
            return false;
        }

        var appModelKeys = new HashSet<ResourceKey>();
        foreach (var resource in _resources.Values)
        {
            if (TelemetryRepository.GetResourceByCompositeName(resource.Name) is { } otlpResource)
            {
                appModelKeys.Add(otlpResource.ResourceKey);
            }
        }

        return telemetryResources.Any(r => !appModelKeys.Contains(r.ResourceKey));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _telemetryResourcesSubscription?.Dispose();
        await _cts.CancelAsync();
        if (_watchTask is not null)
        {
            await _watchTask;
        }
        _cts.Dispose();
    }
}

/// <summary>
/// Which optional navigation entries have something to show.
/// </summary>
public readonly record struct NavigationAvailability(bool HasResourceTerminals, bool HasParameters, bool HasTelemetrySources)
{
    /// <summary>
    /// Used until the live run's app model is loaded, and for runs that aren't watched: entries that are always
    /// listed stay enabled, and terminals, which are only listed when available, stay hidden.
    /// </summary>
    public static NavigationAvailability Default { get; } = new(HasResourceTerminals: false, HasParameters: true, HasTelemetrySources: true);
}
