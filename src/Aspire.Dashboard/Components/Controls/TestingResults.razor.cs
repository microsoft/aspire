// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class TestingResults : ComponentBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resources = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private Task? _subscription;
    private ResourcesLayout? _layout;
    private bool _loaded;
    private bool _subscriptionFailed;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required ILogger<TestingResults> Logger { get; init; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    [Parameter]
    public IReadOnlyCollection<string>? ResourceNames { get; set; }

    internal IReadOnlyList<TestingResourceGroup> Groups
    {
        get
        {
            var selected = ResourcesLayout?.SelectedResourceNames;
            var scope = selected is { Count: > 0 } ? selected : ResourcesLayout?.GetScopeResourceNames();
            var legacySelection = ResourceName is not null ? [ResourceName] : ResourceNames;
            return _resources.Values.Where(r => r.IsTesting && !r.IsResourceHidden(false))
                .Where(r => ResourcesLayout is not null || legacySelection is not { Count: > 0 } ||
                    legacySelection.Contains(r.Name, StringComparers.ResourceName) || legacySelection.Contains(r.DisplayName, StringComparers.ResourceName))
                .Where(r => scope is null || scope.Contains(r.Name, StringComparers.ResourceName) || scope.Contains(r.DisplayName, StringComparers.ResourceName))
                .GroupBy(r => r.DisplayName, StringComparers.ResourceName)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => TestingResourceGroup.Create(g.ToList())).ToList();
        }
    }

    protected override void OnInitialized() => _subscription = WatchAsync(_cts.Token);

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_layout, ResourcesLayout))
        {
            _layout?.ResourcesChanged -= OnResourcesChanged;
            _layout = ResourcesLayout;
            _layout?.ResourcesChanged += OnResourcesChanged;
        }
    }

    private void OnResourcesChanged() => StateHasChanged();

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (DashboardClient.IsEnabled)
            {
                var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
                foreach (var resource in snapshot)
                {
                    _resources[resource.Name] = resource;
                }
                await InvokeAsync(() => { _loaded = true; StateHasChanged(); });
                await foreach (var changes in subscription.WithCancellation(cancellationToken))
                {
                    foreach (var change in changes)
                    {
                        if (change.ChangeType == ResourceViewModelChangeType.Delete)
                        {
                            _resources.TryRemove(change.Resource.Name, out _);
                        }
                        else
                        {
                            _resources[change.Resource.Name] = change.Resource;
                        }
                    }
                    await InvokeAsync(StateHasChanged);
                }
            }
            else
            {
                _loaded = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Watching testing resources failed.");
            await InvokeAsync(() =>
            {
                _subscriptionFailed = true;
                _loaded = true;
                StateHasChanged();
            });
        }
    }

    private string Status(string outcome) => Loc[outcome switch
    {
        "passed" => nameof(Resources.Layout.TestingPassed),
        "failed" => nameof(Resources.Layout.TestingFailed),
        "skipped" => nameof(Resources.Layout.TestingSkipped),
        "inconclusive" => nameof(Resources.Layout.TestingInconclusive),
        "error" => nameof(Resources.Layout.TestingError),
        "timedOut" => nameof(Resources.Layout.TestingTimedOut),
        "running" => nameof(Resources.Layout.TestingRunning),
        "pending" => nameof(Resources.Layout.TestingPending),
        "cancelled" => nameof(Resources.Layout.TestingCancelled),
        _ => nameof(Resources.Layout.TestingUnknown)
    }];

    private static string StatusClass(string outcome) => TestingResultRow.IsFailure(outcome) ? "testing-status-failed" : string.Empty;

    private string Duration(double? duration) => duration is { } milliseconds
        ? string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.TestingMilliseconds)], milliseconds)
        : string.Empty;

    private static IEnumerable<TestingResultRow> GetRows(TestingResourceGroup group) => group.Results.Count > 0
        ? group.Results : [new(group.ResourceName, group.Outcome, null, group.Error)];

    public async ValueTask DisposeAsync()
    {
        _layout?.ResourcesChanged -= OnResourcesChanged;
        _cts.Cancel();
        if (_subscription is not null)
        {
            await _subscription;
        }
        _cts.Dispose();
    }
}
