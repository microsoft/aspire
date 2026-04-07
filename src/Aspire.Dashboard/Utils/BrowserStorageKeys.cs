// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Utils;

internal static class BrowserStorageKeys
{
    public const string UnsecuredTelemetryMessageDismissedKey = "Aspire_Telemetry_UnsecuredMessageDismissed";
    public const string UnsecuredEndpointMessageDismissedKey = "Aspire_Security_UnsecuredEndpointMessageDismissed";
    public const string TimeFormat = "Aspire_TimeFormat";

    public const string TracesPageState = "Aspire_PageState_Traces";
    public const string StructuredLogsPageState = "Aspire_PageState_StructuredLogs";
    public const string MetricsPageState = "Aspire_PageState_Metrics";
    public const string ConsoleLogsPageState = "Aspire_PageState_ConsoleLogs";
    public const string ResourcesPageState = "Resources_PageState";
    public const string ConsoleLogConsoleSettings = "Aspire_ConsoleLog_ConsoleSettings";
    public const string ConsoleLogFilters = "Aspire_ConsoleLog_Filters";
    public const string TextVisualizerDialogSettings = "Aspire_TextVisualizerDialog_TextVisualizerDialogSettings";
    public const string ResourcesShowResourceTypes = "Aspire_Resources_ShowResourceTypes";

    public const string AssistantChatAssistantSettings = "Aspire_AssistantChat_AssistantSettings";
    public const string DashboardTelemetrySettings = "Aspire_Settings_DashboardTelemetry";
    public const string ResourcesShowHiddenResources = "Aspire_Resources_ShowHiddenResources";

    public const string CollapsedResourceNamesKeyPrefix = "Aspire_Resources_CollapsedResourceNames_";
    public const string SplitterOrientationKeyPrefix = "Aspire_SplitterOrientation_";
    public const string SplitterSizeKeyPrefix = "Aspire_SplitterSize_";

    public static string CollapsedResourceNamesKey(string applicationName)
    {
        ArgumentNullException.ThrowIfNull(applicationName);

        var builder = new StringBuilder(applicationName.Length);

        foreach (var c in applicationName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return $"{CollapsedResourceNamesKeyPrefix}{builder.ToString()}";
    }

    public static string SplitterOrientationKey(string viewKey)
    {
        return $"{SplitterOrientationKeyPrefix}{viewKey}";
    }

    public static string SplitterSizeKey(string viewKey, Orientation orientation)
    {
        return $"{SplitterSizeKeyPrefix}{orientation}_{viewKey}";
    }
}
