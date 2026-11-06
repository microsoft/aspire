// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// SPIKE: hidden, infrastructure-only host for the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server process.
/// </summary>
/// <remarks>
/// <para>
/// Usage (constructed by <c>Aspire.Hosting.Dotnet</c>, never by users):
/// <c>aspire __dotnet-watch-server -- &lt;muxer&gt; exec &lt;watcher.dll&gt; server ...</c>.
/// </para>
/// <para>
/// The watcher only reacts to Ctrl+C when its stdin is a console (it registers <c>Console.CancelKeyPress</c> only if
/// input isn't redirected), and DCP always redirects stdin. So the watcher ignores DCP's stop signal and DCP falls back
/// to killing it after its own timeout, 10-50 seconds later, while the watcher can still relaunch services on file
/// changes. This host turns DCP's stop signal into a prompt, bounded shutdown: it gives the watcher a short grace
/// period (during which it terminates services whose proxies already disconnected), then kills the watcher's whole
/// process tree. On Windows the host also puts itself and the watcher in a kill-on-close job, so the tree dies with the
/// host even if the host is terminated without a signal.
/// </para>
/// </remarks>
internal static partial class DotnetWatchServerHost
{
    public const string CommandName = "__dotnet-watch-server";
    public const string ShutdownGraceVariable = "ASPIRE_DOTNET_WATCH_SERVER_SHUTDOWN_GRACE_MS";

    internal const int DefaultShutdownGraceMilliseconds = 1500;
    internal const int ExitUsage = 64;
    internal const int ExitLaunchFailed = 69;

    public static bool Matches(string[] args) => args.Length > 0 && args[0] == CommandName;

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var fileName, out var arguments, out var error))
        {
            Console.Error.WriteLine($"{CommandName}: {error}");
            return ExitUsage;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        var registrations = new List<PosixSignalRegistration>();
        foreach (var signal in ShutdownSignals)
        {
            try
            {
                registrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    shutdown.Cancel();
                }));
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        using var job = OperatingSystem.IsWindows() ? TryCreateKillOnCloseJob() : null;

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            // Same as under DCP: the watcher must not see a console it could read keys from.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"{CommandName}: {ex.Message}");
            return ExitLaunchFailed;
        }

        using (process)
        {
            var stdout = PumpAsync(process.StandardOutput.BaseStream, Console.OpenStandardOutput());
            var stderr = PumpAsync(process.StandardError.BaseStream, Console.OpenStandardError());
            var exited = process.WaitForExitAsync(CancellationToken.None);

            try
            {
                await exited.WaitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, exited, GetShutdownGrace()).ConfigureAwait(false);
            }

            // A detached grandchild (for example a build server node) can keep the output pipes open; don't wait on it.
            await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);

            foreach (var registration in registrations)
            {
                registration.Dispose();
            }

            return shutdown.IsCancellationRequested ? 0 : process.ExitCode;
        }
    }

    internal static PosixSignal[] ShutdownSignals => [PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGQUIT, PosixSignal.SIGHUP];

    internal static async Task StopAsync(Process process, Task exited, TimeSpan grace)
    {
        await Task.WhenAny(exited, Task.Delay(grace)).ConfigureAwait(false);
        if (!exited.IsCompleted)
        {
            try
            {
                // Includes services the watcher launched (or relaunched after a file change during shutdown).
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            await exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    internal static TimeSpan GetShutdownGrace(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(ShutdownGraceVariable);
        return TimeSpan.FromMilliseconds(int.TryParse(value, out var ms) && ms >= 0 ? ms : DefaultShutdownGraceMilliseconds);
    }

    internal static bool TryParse(string[] args, out string fileName, out IReadOnlyList<string> arguments, out string? error)
    {
        fileName = string.Empty;
        arguments = [];
        error = null;

        if (args.Length < 3 || args[1] != "--")
        {
            error = "usage: " + CommandName + " -- <executable> [arguments]";
            return false;
        }

        fileName = args[2];
        arguments = args[3..];
        return true;
    }

    private static async Task PumpAsync(Stream source, Stream destination)
    {
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                await destination.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            await destination.DisposeAsync().ConfigureAwait(false);
        }
    }
}
