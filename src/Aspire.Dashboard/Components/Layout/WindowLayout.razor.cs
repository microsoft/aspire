// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Authentication;
using Aspire.Dashboard.Configuration;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// Provides a chromeless layout for dashboard windows that require authentication in secured modes.
/// </summary>
public partial class WindowLayout
{
    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    [CascadingParameter]
    public Task<AuthenticationState>? AuthenticationState { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await DashboardAuthenticationState.EnsureAuthenticatedAsync(DashboardOptions.Value, AuthenticationState);
    }
}
