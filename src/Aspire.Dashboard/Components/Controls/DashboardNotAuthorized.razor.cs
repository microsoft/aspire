// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Controls;

public sealed partial class DashboardNotAuthorized : ComponentBase
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Routes> Loc { get; init; }

    protected override void OnInitialized()
    {
        if (DashboardOptions.Value.Frontend.AuthMode == FrontendAuthMode.BrowserToken)
        {
            // Preserve the path base in both URLs, and keep the return URL local so login validation accepts it.
            var currentUri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
            var returnUrl = currentUri.PathAndQuery + currentUri.Fragment;
            var loginUri = NavigationManager.ToAbsoluteUri(DashboardUrls.LoginBasePath);
            var loginUrl = QueryHelpers.AddQueryString(loginUri.AbsoluteUri, "returnUrl", returnUrl);

            NavigationManager.NavigateTo(loginUrl, new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }
}
