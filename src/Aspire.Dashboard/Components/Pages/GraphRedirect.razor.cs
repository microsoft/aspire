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

    protected override async Task OnInitializedAsync()
    {
        await ViewPreferences.SetGroupOverviewAsync(GroupOverview.Graph);
        NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
    }
}
