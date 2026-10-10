// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Aspire.Shared;
using StreamJsonRpc;

internal static partial class TerminalHostForwarder
{
    // kill(2) takes the native SIGTERM number (15 on Linux/macOS), not the
    // symbolic value of the .NET PosixSignal.SIGTERM enum.
    private const int SigTerm = 15;

    // Match ParentProcessWatchdog's conventional "terminated by timeout" exit code.
    private const int ShutdownTimeoutExitCode = 124;

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
                        // TerminateProcess cannot run the child's socket cleanup. Ask its
                        // control RPC to stop first, retaining the bounded kill fallback.
                        using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        try
                        {
                            await RequestShutdownAsync(args, shutdownTimeout.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is SocketException or IOException or ArgumentException or OperationCanceledException or ConnectionLostException)
                        {
                            Console.Error.WriteLine($"Failed to request terminal host shutdown: {ex.Message}");
                            if (!process.HasExited)
                            {
                                process.Kill(entireProcessTree: true);
                            }
                        }
                    }
                    else if (SendSignal(process.Id, SigTerm) != 0 && !process.HasExited)
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
                    return ShutdownTimeoutExitCode;
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

    internal static async Task RequestShutdownAsync(string[] args, CancellationToken cancellationToken)
    {
        // TerminalHost receives the control socket as two arguments: --control-uds <path>.
        var controlPathIndex = Array.IndexOf(args, "--control-uds");
        if (controlPathIndex < 0 || controlPathIndex + 1 >= args.Length)
        {
            throw new ArgumentException("A terminal host control socket path is required for graceful shutdown.", nameof(args));
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(args[controlPathIndex + 1]), cancellationToken).ConfigureAwait(false);
        using var stream = new NetworkStream(socket);
        using var rpc = new JsonRpc(stream);
        rpc.StartListening();
        await rpc.NotifyAsync("shutdown").WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SendSignal(int processId, int signal);
}
