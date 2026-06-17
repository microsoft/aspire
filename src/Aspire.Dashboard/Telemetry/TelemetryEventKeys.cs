// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Telemetry;

public static class TelemetryEventKeys
{
    private const string AspireDashboardEventPrefix = "aspire/dashboard/";

    public const string ComponentInitialize = AspireDashboardEventPrefix + "component/initialize";
    public const string ParametersSet = AspireDashboardEventPrefix + "component/paramsSet";
    public const string ComponentDispose = AspireDashboardEventPrefix + "component/dispose";

    public const string Error = AspireDashboardEventPrefix + "error";

    public const string ExecuteCommand = AspireDashboardEventPrefix + "command";

    public const string AIAssistantFeedback = AspireDashboardEventPrefix + "aiassistant/feedback";
}
