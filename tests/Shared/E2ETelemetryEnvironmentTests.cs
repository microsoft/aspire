// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Tests.Shared;
using Xunit;

namespace Aspire.Tests.Utils;

internal static class E2ETelemetryEnvironmentTests
{
    internal static async Task ChildProcessesInheritOptOutAndCanOverrideIt()
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

    internal static async Task ShellEnvironmentSetupOptsOutOfProductTelemetry(bool includeBundlePath)
    {
        var startInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "false";
        startInfo.Environment["ASPIRE_DASHBOARD_TELEMETRY_OPTOUT"] = "false";
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            AspireCliShellCommandHelpers.GetSourceAspireEnvironmentCommand(includeBundlePath) +
            """; printf '%s %s\n' "$ASPIRE_CLI_TELEMETRY_OPTOUT" "$ASPIRE_DASHBOARD_TELEMETRY_OPTOUT" """);

        using var process = Process.Start(startInfo)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, await errorTask);
        Assert.Equal("true true\n", await outputTask);
    }
}
