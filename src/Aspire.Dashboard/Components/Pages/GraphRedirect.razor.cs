// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Preserves existing graph deep links while the graph is displayed as an Overview mode.
/// </summary>
public sealed partial class GraphRedirect : ComponentBase
{
    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    protected override async Task OnInitializedAsync()
    {
        await ViewPreferences.SetGroupOverviewAsync(GroupOverview.Graph);
        // The link asks for the graph, so the resources open on their overview rather than on the full-page list the
        // dashboard otherwise opens on (see ResourcesLayout.TryRedirectLandingAsync). It goes through session storage,
        // not the navigation's history entry state: this runs as the page loads, before the router intercepts
        // navigation, so Blazor redirects with a full page load, which drops the history entry state.
        await SessionStorage.SetAsync(BrowserStorageKeys.ResourceContentOpen, true);
        NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
    }
}
