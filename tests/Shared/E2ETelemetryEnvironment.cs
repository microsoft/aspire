// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Xunit;

namespace Aspire.Tests.Utils;

internal static class E2ETelemetryEnvironment
{
    internal const string CliOptOut = "ASPIRE_CLI_TELEMETRY_OPTOUT";
    internal const string DashboardOptOut = "ASPIRE_DASHBOARD_TELEMETRY_OPTOUT";

    internal static async Task VerifyChildProcessesInheritOptOutAndCanOverrideItAsync()
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            """printf '%s %s\n' "$ASPIRE_CLI_TELEMETRY_OPTOUT" "$ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"; """ +
            """env ASPIRE_CLI_TELEMETRY_OPTOUT=false ASPIRE_DASHBOARD_TELEMETRY_OPTOUT=false /bin/sh -c 'printf "%s %s\n" "$ASPIRE_CLI_TELEMETRY_OPTOUT" "$ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"'""");

        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await errorTask);
        Assert.Equal("true true\nfalse false\n", await outputTask);
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        // Set these before tests launch child processes; local OTLP profiling and application
        // telemetry remain enabled independently of reported product telemetry.
        Environment.SetEnvironmentVariable(CliOptOut, "true");
        Environment.SetEnvironmentVariable(DashboardOptOut, "true");
    }
}
