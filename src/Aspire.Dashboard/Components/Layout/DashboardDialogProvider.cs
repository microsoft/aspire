// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Authentication;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// Renders the Fluent dialog provider and dismisses open dialogs when navigation changes.
/// </summary>
[Authorize(Policy = FrontendAuthorizationDefaults.PolicyName)]
public class DashboardDialogProvider : ComponentBase, IDisposable
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required NavigationDialogService DialogService { get; init; }

    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    [CascadingParameter]
    public Task<AuthenticationState>? AuthenticationState { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await DashboardAuthenticationState.EnsureAuthenticatedAsync(DashboardOptions.Value, AuthenticationState);
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<FluentDialogProvider>(0);
        builder.CloseComponent();
    }

    private async void OnLocationChanged(object? sender, LocationChangedEventArgs args)
    {
        try
        {
            await InvokeAsync(DialogService.DismissAllAsync);
        }
        catch (Exception exception)
        {
            await DispatchExceptionAsync(exception);
        }
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}