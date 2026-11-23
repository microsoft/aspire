// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

internal readonly record struct TrayProcessIdentity(int ProcessId, long StartTimeUtcTicks)
{
    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(ProcessId);
        }
        catch (ArgumentException)
        {
            return;
        }
        using (process)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    // Pin the kernel process object before checking creation time. Otherwise
                    // Process can reopen the PID for the wait after Windows has reused it.
                    _ = process.SafeHandle;
                }
                catch (InvalidOperationException) when (process.HasExited)
                {
                    return;
                }
            }
            if (process.HasExited)
            {
                return;
            }
            try
            {
                if (process.StartTime.ToUniversalTime().Ticks != StartTimeUtcTicks)
                {
                    return;
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                return;
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
