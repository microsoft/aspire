// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Claims;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Controls;

public partial class UserProfile : ComponentBase
{
    [Inject]
    public required IOptionsMonitor<DashboardOptions> DashboardOptions { get; init; }

    [Inject]
    public required IStringLocalizer<Login> Loc { get; init; }

    [Inject]
    public required ILogger<UserProfile> Logger { get; init; }

    [CascadingParameter]
    public required Task<AuthenticationState> AuthenticationState { get; set; }

    [Parameter]
    public string ButtonSize { get; set; } = "24px";

    [Parameter]
    public string ImageSize { get; set; } = "52px";

    private bool _showUserProfileMenu;
    private bool _profileMenuOpen;
    private readonly string _profileButtonId = $"profile-btn-{Guid.NewGuid():N}";
    private string? _name;
    private string? _username;
    private string? _initials;

    protected override async Task OnParametersSetAsync()
    {
        if (DashboardOptions.CurrentValue.Frontend.AuthMode == FrontendAuthMode.OpenIdConnect)
        {
            var authState = await AuthenticationState;

            var claimsIdentity = authState.User.Identity as ClaimsIdentity;

            if (claimsIdentity?.IsAuthenticated == true)
            {
                _showUserProfileMenu = true;
                _name = claimsIdentity.FindFirst(DashboardOptions.CurrentValue.Frontend.OpenIdConnect.GetNameClaimTypes());
                if (string.IsNullOrWhiteSpace(_name))
                {
                    // Make sure there's always a name, even if that name is a placeholder
                    _name = Loc[nameof(Login.AuthorizedUser)];
                }

                _username = claimsIdentity.FindFirst(DashboardOptions.CurrentValue.Frontend.OpenIdConnect.GetUsernameClaimTypes());
                _initials = _name.GetInitials();
            }
            else
            {
                // If we don't have an authenticated user, don't show the user profile menu. This shouldn't happen.
                _showUserProfileMenu = false;
                _name = null;
                _username = null;
                _initials = null;
                Logger.LogError("Dashboard:Frontend:AuthMode is configured for OpenIDConnect, but there is no authenticated user.");
            }
        }
    }
}
