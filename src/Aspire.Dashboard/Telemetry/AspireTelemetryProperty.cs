// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Telemetry;

public record struct AspireTelemetryProperty(object Value, AspireTelemetryPropertyType PropertyType = AspireTelemetryPropertyType.Basic);

public enum AspireTelemetryPropertyType
{
    Pii = 0,
    Basic = 1,
    Metric = 2,
    UserSetting = 3
}
