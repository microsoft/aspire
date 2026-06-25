// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.ServiceClient;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Humanizer;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Top-level page listing the application's parameter resources, mirroring Deck's Parameters
/// page. Parameters previously lived in a tab inside the Resources page; here they get their
/// own nav entry and a Deck-styled table with sensitive-value masking.
/// </summary>
public sealed partial class Parameters : ComponentBase, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private Task? _subscriptionTask;
    private bool _loaded;
    private string? _filter;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Columns> ColumnsLoc { get; init; }

    protected override async Task OnInitializedAsync()
    {
        if (!DashboardClient.IsEnabled)
        {
            _loaded = true;
            return;
        }

        var (snapshot, subscription) = await DashboardClient.SubscribeResourcesAsync(_cts.Token);

        foreach (var resource in snapshot)
        {
            _resourceByName[resource.Name] = resource;
        }

        _loaded = true;

        _subscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        _resourceByName[resource.Name] = resource;
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                    }
                }

                await InvokeAsync(StateHasChanged);
            }
        });
    }

    private List<ResourceViewModel> GetFilteredParameters()
    {
        var trimmed = _filter?.Trim();
        return _resourceByName.Values
            .Where(r => r.IsParameter && !r.IsResourceHidden(showHiddenResources: false))
            .Where(r => string.IsNullOrEmpty(trimmed) || r.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetStateText(ResourceViewModel resource)
        => ResourceStateViewModel.GetStateViewModel(resource, ColumnsLoc).Text;

    private string FormatStarted(ResourceViewModel resource)
    {
        if (resource.StartTimeStamp is not { } started)
        {
            return "—";
        }

        // Render as a relative "x ago" string, matching the compact Deck started column.
        return (TimeProvider.GetUtcNow() - started).Humanize(culture: System.Globalization.CultureInfo.CurrentCulture) is var ago && !string.IsNullOrEmpty(ago)
            ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{ago} ago")
            : "—";
    }

    // Resolves the displayed parameter value. Mirrors the logic the Resources page uses for the
    // parameter value column: unresolved parameters (not yet running) hide their value, and the
    // value carries a sensitivity flag so secrets can be masked.
    private static (string? Value, bool IsSensitive, bool IsUnresolved) GetParameterValue(ResourceViewModel resource)
    {
        var isUnresolved = !resource.IsRunningState();

        if (resource.Properties.TryGetValue(KnownProperties.Parameter.Value, out var property))
        {
            var value = isUnresolved ? null : (property.Value.HasStringValue ? property.Value.StringValue : null);
            return (value, property.IsValueSensitive, isUnresolved);
        }

        return (null, false, isUnresolved);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_subscriptionTask);
    }
}
