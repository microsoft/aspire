// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Telemetry;

namespace Aspire.Dashboard.Components;

/// <summary>
/// Interface for components that would like to opt-in to telemetry events, capturing
/// 1) initialization
/// 2) component properties on each render
/// 3) component disposal
/// </summary>
public interface IComponentWithTelemetry
{
    public ComponentTelemetryContextProvider TelemetryContextProvider { get; }
    public ComponentTelemetryContext TelemetryContext { get; }

    public void UpdateTelemetryProperties()
    {
    }
}

public record struct ComponentTelemetryProperty(string Name, AspireTelemetryProperty Value);
