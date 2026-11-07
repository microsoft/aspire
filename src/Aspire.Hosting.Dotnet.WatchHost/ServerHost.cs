// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Aspire.Shared;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.WatchHost;

/// <summary>
/// Hosts the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server process for the hidden watch server resource.
/// </summary>
/// <remarks>
/// <para>
/// Usage (constructed by <c>Aspire.Hosting.Dotnet</c>, never by users):
/// <c>dotnet exec Aspire.Hosting.Dotnet.WatchHost.dll server -- &lt;muxer&gt; exec &lt;watcher.dll&gt; server ...</c>.
/// </para>
/// <para>
/// The watcher only reacts to Ctrl+C when its stdin is a console, and DCP always redirects stdin. So the watcher ignores
/// DCP's stop signal and DCP kills it after its own timeout, 10-50 seconds later, while the watcher can still relaunch
/// services on file changes. This host turns DCP's stop signal into a prompt, bounded shutdown: it gives the watcher a
/// short grace period (during which it terminates services whose proxies already disconnected), then kills the
/// watcher's whole process tree.
/// </para>
/// <para>
/// The watcher runs under the repo's process supervisor (<see cref="ProcessSupervisor"/>, this assembly re-executed as
/// a guardian), so it and the services it launched also die if this host is killed without a signal: the guardian
/// holds them in a kill-on-close job object on Windows, and in its own process group on Unix, which it kills when this
/// host exits. That works on .NET 10 on every platform, macOS included.
/// </para>
/// </remarks>
internal static class ServerHost
{
    private const string LogPrefix = "dotnet-watch server";

    internal const int DefaultShutdownGraceMilliseconds = 1500;
    internal const int ExitUsage = 64;
    internal const int ExitLaunchFailed = 69;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--", var fileName, .. var arguments])
        {
            Console.Error.WriteLine($"{LogPrefix}: usage: {DotnetWatchHostProtocol.ServerCommand} -- <executable> [arguments]");
            return ExitUsage;
        }

        using var shutdown = new ShutdownSignal();

        var watcher = new ProcessStartInfo(fileName);
        foreach (var argument in arguments)
        {
            watcher.ArgumentList.Add(argument);
        }

        var directory = Directory.CreateTempSubdirectory("aspire-watch-host-");
        var exitCodePath = Path.Combine(directory.FullName, "exit-code");
        var cleanupMarker = Environment.GetEnvironmentVariable(DotnetWatchHostProtocol.CleanupMarkerVariable);
        watcher.Environment.Remove(DotnetWatchHostProtocol.CleanupMarkerVariable);
        foreach (var name in DotnetWatchOwnership.PrivateEnvironmentVariables)
        {
            watcher.Environment.Remove(name);
        }
        foreach (var name in new[] { "DEBUG_SESSION_PORT", "DEBUG_SESSION_TOKEN", "DEBUG_SESSION_INFO", "DEBUG_SESSION_SERVER_URI" })
        {
            if (watcher.Environment.TryGetValue(name, out var value) && string.IsNullOrEmpty(value))
            {
                watcher.Environment.Remove(name);
            }
        }
        var diagnostics = new DiagnosticWriter(Console.Error);
        using var grace = new CancellationTokenSource();
        var options = new ChildProcessOptions
        {
            Lifetime = ChildProcessLifetime.OwnedTree,
            CompletionPath = exitCodePath,
            BeginGracefulShutdown = () =>
            {
                grace.CancelAfter(GetShutdownGrace());
                return grace.Token;
            },
            // The watcher ignores redirected-console signals. Let disconnected proxies
            // terminate their applications during the grace period, then contain and reap
            // the entire scope through ChildProcess rather than trusting the host's PID exit.
            RequestGracefulShutdownAsync = (_, _) => Task.CompletedTask,
            StandardOutputCallback = line => Console.Out.WriteLine(line),
            StandardErrorCallback = line =>
            {
                if (!ProcessSupervisorLogger.TryForward(line, diagnostics))
                {
                    Console.Error.WriteLine(line);
                }
            }
        };
        var process = new ChildProcess(watcher, diagnostics, options, OperatingSystem.IsWindows());
        await using var processLifetime = process.ConfigureAwait(false);
        try
        {
            await process.StartAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var code = await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                WriteCleanupMarker(cleanupMarker);
                return code;
            }
            catch (OperationCanceledException) when (shutdown.Token.IsCancellationRequested)
            {
                // WaitForExitAsync has already verified contained termination on cancellation.
                WriteCleanupMarker(cleanupMarker);
                return 0;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            Console.Error.WriteLine($"{LogPrefix}: {ex.Message}");
            return ExitLaunchFailed;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void WriteCleanupMarker(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, "contained-cleanup-complete");
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
            // Includes the watcher and the services it launched (or relaunched after a file change during shutdown).
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    internal static TimeSpan GetShutdownGrace(string? value = null)
    {
        value ??= Environment.GetEnvironmentVariable(DotnetWatchHostProtocol.ShutdownGraceVariable);
        return TimeSpan.FromMilliseconds(int.TryParse(value, out var ms) && ms >= 0 ? ms : DefaultShutdownGraceMilliseconds);
    }

    internal static int? ReadExitCode(string path)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class DiagnosticWriter(TextWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                writer.WriteLine($"{LogPrefix}: {formatter(state, exception)}");
                writer.Flush();
            }
        }
    }
}
