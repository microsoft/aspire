// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Shared;

internal static partial class TerminalHostForwarder
{
    internal static ProcessStartInfo CreateStartInfo(string managedDirectory, string[] args)
    {
        var directory = Path.GetFullPath(Path.Combine(managedDirectory, "..", BundleDiscovery.TerminalHostDirectoryName));
        var startInfo = new ProcessStartInfo(Path.Combine(directory, BundleDiscovery.GetExecutableFileName(BundleDiscovery.TerminalHostExecutableName)))
        {
            WorkingDirectory = directory,
            UseShellExecute = false
        };
        foreach (var argument in args)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The shim watches the original AppHost; its child watches the shim so even an
        // uncatchable termination of the forwarding process cannot orphan the native relay.
        startInfo.Environment[KnownConfigNames.TerminalHostParentProcessId] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        startInfo.Environment[KnownConfigNames.TerminalHostParentProcessStartedStable] =
            ProcessStartTimeHelper.GetCurrentProcessStartTimeUnixMilliseconds().ToString(CultureInfo.InvariantCulture);
        return startInfo;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var startInfo = CreateStartInfo(AppContext.BaseDirectory, args);
        if (!File.Exists(startInfo.FileName))
        {
            Console.Error.WriteLine($"Terminal host executable was not found at '{startInfo.FileName}'. Reinstall or rebuild the Aspire bundle.");
            return 1;
        }

        using var shutdownCts = new CancellationTokenSource();
        var watchdog = ParentProcessWatchdog.Start(shutdownCts,
            KnownConfigNames.TerminalHostParentProcessId, KnownConfigNames.TerminalHostParentProcessStartedStable, legacyStartVariable: null);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, HandleSignal);
            using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, HandleSignal);
            using var sigQuit = OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGQUIT, HandleSignal) : null;
            try
            {
                await process.WaitForExitAsync(shutdownCts.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    else if (SendSignal(process.Id, 15) != 0 && !process.HasExited)
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }

                try
                {
                    await process.WaitForExitAsync().WaitAsync(ParentProcessWatchdog.ForceExitGracePeriod).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Console.Error.WriteLine("Terminal host did not stop within the shutdown grace period.");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    return 124;
                }

                return process.ExitCode;
            }

            void HandleSignal(PosixSignalContext context)
            {
                context.Cancel = !shutdownCts.IsCancellationRequested;
                shutdownCts.Cancel();
            }
        }
        finally
        {
            if (watchdog is not null)
            {
                await watchdog.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
