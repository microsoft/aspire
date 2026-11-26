// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Aspire.Tests.Utils;

internal static class E2ETelemetryEnvironment
{
    internal const string CliOptOut = "ASPIRE_CLI_TELEMETRY_OPTOUT";
    internal const string DashboardOptOut = "ASPIRE_DASHBOARD_TELEMETRY_OPTOUT";

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Set these before tests launch child processes; local OTLP profiling and application
        // telemetry remain enabled independently of reported product telemetry.
        Environment.SetEnvironmentVariable(CliOptOut, "true");
        Environment.SetEnvironmentVariable(DashboardOptOut, "true");
    }
}
