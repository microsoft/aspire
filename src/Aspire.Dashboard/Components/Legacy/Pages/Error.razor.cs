// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using System.Diagnostics;
using Aspire.Dashboard.Telemetry;

namespace Aspire.Dashboard.Components.Legacy.Pages;

public partial class Error : IComponentWithTelemetry, IDisposable
{
    [Inject]
    public required ILogger<Error> Logger { get; init; }

    [CascadingParameter]
    private HttpContext? HttpContext { get; set; }

    private string? RequestId { get; set; }
    private bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        RequestId = Activity.Current?.Id ?? HttpContext?.TraceIdentifier;
    }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.Error);

    protected override void OnParametersSet()
    {
        UpdateTelemetryProperties();
    }

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.ErrorRequestId, new AspireTelemetryProperty(RequestId ?? string.Empty)),
        ], Logger);
    }

    public void Dispose()
    {
        TelemetryContext.Dispose();
    }
}
