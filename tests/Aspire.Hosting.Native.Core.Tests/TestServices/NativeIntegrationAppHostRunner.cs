// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal static class NativeIntegrationAppHostRunner
{
    public static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string token, TimeProvider clock)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["ASPIRE_NATIVE_RPC_TOKEN"] = token;

        return await RunAsync(start, clock).ConfigureAwait(false);
    }

    public static async Task<string> RunAsync(ProcessStartInfo start, TimeProvider clock)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The AppHost did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45), clock);
            var error = await stderr;
            Assert.True(process.ExitCode == 0, error);

            return await stdout;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
