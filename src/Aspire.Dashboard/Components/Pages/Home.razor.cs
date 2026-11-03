// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// The root URL. It redirects to the resources, with all resources selected, whose overview summarizes the app.
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
        var url = TryGetLegacyRedirectUrl(LegacyResourceName, LegacyViewKind)
            ?? (DashboardClient.IsEnabled ? DashboardUrls.ResourceListUrl() : DashboardUrls.StructuredLogsUrl());
        NavigationManager.NavigateTo(url, new NavigationOptions { ReplaceHistoryEntry = true });
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
