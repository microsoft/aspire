// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
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
    public required DashboardDataSourcePool DataSourcePool { get; init; }

    protected override void OnInitialized()
    {
        NavigationManager.LocationChanged += OnLocationChanged;
        DataSourcePool.CurrentRunChanged += OnCurrentRunChanged;
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<FluentDialogProvider>(0);
        builder.CloseComponent();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs args) => DismissDialogs();

    private void OnCurrentRunChanged() => DismissDialogs();

    private async void DismissDialogs()
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
        DataSourcePool.CurrentRunChanged -= OnCurrentRunChanged;
    }
}