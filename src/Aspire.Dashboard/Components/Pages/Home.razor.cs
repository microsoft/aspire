// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// The root URL, the dashboard's home. It shows every resource as a table that fills the page.
/// </summary>
/// <remarks>
/// <para>
/// The table is the resource list of <see cref="Layout.ResourcesLayout"/> at full width, not a view of a
/// selection, which is why it is its own page rather than part of the overview at <c>/resources</c>.
/// </para>
/// <para>
/// Some requests for the root URL are for another page, and are redirected to it:
/// links created by older tools, such as <c>/?resource=api</c> or <c>/?view=Graph</c>; and a dashboard without a
/// resource service, which opens the telemetry overview. The viewport width doesn't redirect: a narrow layout
/// shows home as the open resource drawer, so resizing the window keeps the user on home.
/// </para>
/// </remarks>
public sealed partial class Home : ComponentBase
{
    private string? _redirectedTo;

    [SupplyParameterFromQuery(Name = "resource")]
    public string? LegacyResourceName { get; set; }

    [SupplyParameterFromQuery(Name = "view")]
    public string? LegacyViewKind { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    // Not OnInitialized: the query parameters are set again when only the query string of "/" changes.
    protected override void OnParametersSet()
    {
        var url = TryGetLegacyRedirectUrl(LegacyResourceName, LegacyViewKind)
            ?? (!DashboardClient.IsEnabled ? DashboardUrls.ResourceListUrl() : null);

        // The layout re-renders this page while the redirect is in flight. Ask for it once.
        if (url is null || string.Equals(url, _redirectedTo, StringComparison.Ordinal))
        {
            return;
        }

        _redirectedTo = url;
        NavigationManager.NavigateTo(url, new NavigationOptions { ReplaceHistoryEntry = true });
    }

    // Older links select the view with "?view=Graph" or "?view=Parameters". Any other value, such as "Table", is the
    // resource table.
    private const string LegacyGraphViewKind = "Graph";
    private const string LegacyParametersViewKind = "Parameters";

    internal static string? TryGetLegacyRedirectUrl(string? resourceName, string? viewKind)
    {
        if (string.Equals(viewKind, LegacyGraphViewKind, StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.GraphUrl();
        }

        if (string.Equals(viewKind, LegacyParametersViewKind, StringComparison.OrdinalIgnoreCase))
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
