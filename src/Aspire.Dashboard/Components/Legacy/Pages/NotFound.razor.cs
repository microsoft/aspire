// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Telemetry;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Legacy.Pages;

public partial class NotFound : IComponentWithTelemetry, IDisposable
{
    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.NotFound);

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
    }

    public void Dispose()
    {
        TelemetryContext.Dispose();
    }
}
