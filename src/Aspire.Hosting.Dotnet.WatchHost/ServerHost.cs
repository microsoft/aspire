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

    // How long output pumps may lag behind the watcher's exit before the host stops waiting for them.
    private static readonly TimeSpan s_outputDrainTimeout = TimeSpan.FromSeconds(5);

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

        // On Unix the guardian's own exit status is the signal that tore down its process group, so it hands the
        // watcher's exit code over in a private file instead.
        var exitCodePath = Path.GetTempFileName();
        var startInfo = ProcessSupervisor.CreateStartInfo(watcher, exitCodePath);

        // Like under DCP, the watcher must not see a console it could read keys from: its stdin is an empty pipe.
        startInfo.RedirectStandardInput = true;

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Win32Exception ex)
        {
            File.Delete(exitCodePath);
            Console.Error.WriteLine($"{LogPrefix}: {ex.Message}");
            return ExitLaunchFailed;
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = Task.WhenAll(
                PumpAsync(process.StandardOutput, Console.Out, forwardSupervisorDiagnostics: false),
                PumpAsync(process.StandardError, Console.Error, forwardSupervisorDiagnostics: true));

            try
            {
                await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
                await DrainAsync(output).ConfigureAwait(false);
                // No handed-off code means the guardian never ran the watcher to completion (for example, it couldn't
                // start it); the guardian already reported why on stderr.
                return ReadExitCode(exitCodePath) ?? ExitLaunchFailed;
            }
            catch (OperationCanceledException)
            {
                await StopAsync(process, GetShutdownGrace()).ConfigureAwait(false);
                await DrainAsync(output).ConfigureAwait(false);
                return 0;
            }
            finally
            {
                File.Delete(exitCodePath);
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

    private static async Task PumpAsync(StreamReader reader, TextWriter writer, bool forwardSupervisorDiagnostics)
    {
        var diagnostics = new DiagnosticWriter(writer);
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            // The guardian's own records share stderr with the watcher; only its warnings and errors are worth showing.
            if (forwardSupervisorDiagnostics && ProcessSupervisorLogger.TryForward(line, diagnostics))
            {
                continue;
            }

            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task DrainAsync(Task output)
    {
        try
        {
            await output.WaitAsync(s_outputDrainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A surviving descendant holds the pipe; the watcher's own output is complete.
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
