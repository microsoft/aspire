// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

public partial class ErrorMode : IComponentWithTelemetry, IDisposable
{
    [Inject]
    public required ILogger<ErrorMode> Logger { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required DashboardErrorMode DashboardErrorModeService { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.ErrorMode);

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);

        // If there are no errors, redirect to the main page
        if (!DashboardErrorModeService.IsErrorMode)
        {
            NavigationManager.NavigateTo("/", replace: true);
        }
    }

    protected override void OnParametersSet()
    {
        UpdateTelemetryProperties();
    }

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.ErrorCount, new AspireTelemetryProperty(DashboardErrorModeService.ValidationFailures.Count)),
        ], Logger);
    }

    private void DismissAndContinue()
    {
        Logger.LogInformation("User dismissed error mode with {ErrorCount} validation failures.", DashboardErrorModeService.ValidationFailures.Count);
        DashboardErrorModeService.Dismiss();
        
        // Navigate to the main page
        NavigationManager.NavigateTo("/", forceLoad: true);
    }

    public void Dispose()
    {
        TelemetryContext.Dispose();
    }
}
