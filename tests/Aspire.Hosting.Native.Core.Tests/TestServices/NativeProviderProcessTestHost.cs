// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class NativeProviderProcessTestHost(Process process, Process? descendant) : IAsyncDisposable
{
    public Process Process { get; } = process;
    public Process? Descendant { get; } = descendant;

    public static async Task<NativeProviderProcessTestHost> StartAsync(bool retainOutput)
    {
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        // The shell exits while its owned sleep child retains both redirected
        // pipes. stdout contains only the child's decimal PID followed by '\n'.
        start.ArgumentList.Add(retainOutput ? "sleep 60 & printf '%s\\n' \"$!\"; exit 0" : "exit 0");
        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("The provider fixture did not start.");
        Process? descendant = null;
        try
        {
            if (retainOutput)
            {
                var line = await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                    ?? throw new InvalidDataException("The provider fixture did not report its descendant.");
                descendant = System.Diagnostics.Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture));
            }
            await process.WaitForExitStatusAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            return new(process, descendant);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                }
                await process.WaitForExitStatusAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await new NativeProviderProcessTestHost(process, descendant).DisposeAsync();
            }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Descendant is { } child)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill();
                    }
                }
                catch (InvalidOperationException) when (child.HasExited)
                {
                }
                // Unix cannot retrieve an exit status for this non-child process.
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            Descendant?.Dispose();
            Process.Dispose();
        }
    }
}
