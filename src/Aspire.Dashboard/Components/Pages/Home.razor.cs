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
/// links created by older tools, such as <c>/?resource=api</c> or <c>/?view=Graph</c>; a dashboard without a
/// resource service, which has no resources to list; and narrow layouts, where the list is a drawer over a page
/// rather than a page of its own, so they open on the overview of all resources.
/// </para>
/// </remarks>
public sealed partial class Home : ComponentBase
{
    private string? _redirectedTo;

    [SupplyParameterFromQuery(Name = "resource")]
    public string? LegacyResourceName { get; set; }

    [SupplyParameterFromQuery(Name = "view")]
    public string? LegacyViewKind { get; set; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    // Not OnInitialized: the viewport is a cascading value, and a window narrowed while the table is showing must
    // move to the overview too, or the page would be left empty.
    protected override void OnParametersSet()
    {
        var url = TryGetLegacyRedirectUrl(LegacyResourceName, LegacyViewKind)
            ?? (!DashboardClient.IsEnabled ? DashboardUrls.StructuredLogsUrl()
                : !ViewportInformation.IsDesktop ? DashboardUrls.ResourceListUrl()
                : null);

        // The layout re-renders this page while the redirect is in flight. Ask for it once.
        if (url is null || string.Equals(url, _redirectedTo, StringComparison.Ordinal))
        {
            return;
        }

        _redirectedTo = url;
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
