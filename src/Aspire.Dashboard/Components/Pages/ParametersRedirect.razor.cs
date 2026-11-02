// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

public sealed partial class ParametersRedirect : ComponentBase
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    protected override void OnInitialized()
    {
        NavigationManager.NavigateTo(DashboardUrls.ResourceListUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
    }
}
