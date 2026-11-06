// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Cli.DotnetWatch;

/// <summary>
/// Hidden, infrastructure-only host for the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server process.
/// </summary>
/// <remarks>
/// <para>
/// Usage (constructed by <c>Aspire.Hosting.Dotnet</c>, never by users):
/// <c>aspire __dotnet-watch-server -- &lt;muxer&gt; exec &lt;watcher.dll&gt; server ...</c>.
/// </para>
/// <para>
/// The watcher only reacts to Ctrl+C when its stdin is a console, and DCP always redirects stdin. So the watcher ignores
/// DCP's stop signal and DCP kills it after its own timeout, 10-50 seconds later, while the watcher can still relaunch
/// services on file changes. This host turns DCP's stop signal into a prompt, bounded shutdown: it gives the watcher a
/// short grace period (during which it terminates services whose proxies already disconnected), then kills the
/// watcher's whole process tree. The watcher is started with <see cref="ProcessStartInfo.KillOnParentExit"/>, so it
/// also dies if this host is terminated without a signal; this host's own exit code is unaffected.
/// </para>
/// </remarks>
internal static class DotnetWatchServerHost
{
    public const string CommandName = "__dotnet-watch-server";
    public const string ShutdownGraceVariable = "ASPIRE_DOTNET_WATCH_SERVER_SHUTDOWN_GRACE_MS";

    internal const int DefaultShutdownGraceMilliseconds = 1500;
    internal const int ExitUsage = 64;
    internal const int ExitLaunchFailed = 69;

    public static bool Matches(string[] args) => args is [CommandName, ..];

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not [_, "--", var fileName, .. var arguments])
        {
            Console.Error.WriteLine($"{CommandName}: usage: {CommandName} -- <executable> [arguments]");
            return ExitUsage;
        }

        using var shutdown = new ShutdownSignal();
        using var nullInput = File.OpenNullHandle();

        // Like under DCP, the watcher must not see a console it could read keys from. Its output goes straight to ours.
        var startInfo = new ProcessStartInfo(fileName, arguments) { StandardInputHandle = nullInput };
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            startInfo.KillOnParentExit = true;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception ex)
        {
            Console.Error.WriteLine($"{CommandName}: {ex.Message}");
            return ExitLaunchFailed;
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                return process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, GetShutdownGrace()).ConfigureAwait(false);
                return 0;
            }
        }
    }

    internal static async Task StopAsync(Process process, TimeSpan grace)
    {
        using var timeout = new CancellationTokenSource(grace);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Includes services the watcher launched (or relaunched after a file change during shutdown).
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    internal static TimeSpan GetShutdownGrace(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(ShutdownGraceVariable);
        return TimeSpan.FromMilliseconds(int.TryParse(value, out var ms) && ms >= 0 ? ms : DefaultShutdownGraceMilliseconds);
    }
}
