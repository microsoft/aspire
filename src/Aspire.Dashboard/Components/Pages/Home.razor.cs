// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// The root URL. It redirects to the resources, with all resources selected, shown as the full resource list.
/// </summary>
/// <remarks>
/// The root URL used to host the resources page. Links created by older tools, such as
/// <c>/?resource=api</c> or <c>/?view=Graph</c>, are redirected to the equivalent view.
/// </remarks>
public sealed partial class Home : ComponentBase
{
    [SupplyParameterFromQuery(Name = "resource")]
    public string? LegacyResourceName { get; set; }

    [SupplyParameterFromQuery(Name = "view")]
    public string? LegacyViewKind { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    protected override void OnInitialized()
    {
        var legacyUrl = TryGetLegacyRedirectUrl(LegacyResourceName, LegacyViewKind);
        var url = legacyUrl ?? (DashboardClient.IsEnabled ? DashboardUrls.ResourceListUrl() : DashboardUrls.StructuredLogsUrl());
        NavigationManager.NavigateTo(url, new NavigationOptions
        {
            ReplaceHistoryEntry = true,
            // The resources URL opens as the full resource list, the dashboard's home.
            HistoryEntryState = legacyUrl is null && DashboardClient.IsEnabled ? Aspire.Dashboard.Components.Layout.ResourcesLayout.ResourceListHomeHistoryEntryState : null
        });
    }

    internal static string? TryGetLegacyRedirectUrl(string? resourceName, string? viewKind)
    {
        if (string.Equals(viewKind, nameof(Resources.ResourceViewKind.Graph), StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.GraphUrl();
        }

        if (string.Equals(viewKind, nameof(Resources.ResourceViewKind.Parameters), StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.ResourceListUrl();
        }

        if (!string.IsNullOrEmpty(resourceName))
        {
            return DashboardUrls.ResourceOverviewUrl(resourceName);
        }

        return null;
    }
}
