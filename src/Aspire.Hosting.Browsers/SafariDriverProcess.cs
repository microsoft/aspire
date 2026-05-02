// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal interface ISafariDriverProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Uri WebDriverEndpoint { get; }

    Task<BrowserLogsProcessResult> ProcessTask { get; }
}

internal static class SafariDriverProcessLauncher
{
    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public static ISafariDriverProcess Start(string driverPath, ILogger<BrowserLogsSessionManager> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverPath);
        ArgumentNullException.ThrowIfNull(logger);

        var port = GetAvailableLoopbackPort();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = driverPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        // safaridriver hosts the classic WebDriver HTTP endpoint. Safari's BiDi websocket is not known until after a
        // WebDriver New Session call requests "webSocketUrl": true, so the AppHost first owns a local driver process
        // addressed through 127.0.0.1 and then upgrades to BiDi through SafariWebDriverClient.
        process.StartInfo.ArgumentList.Add("--port");
        process.StartInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Unable to start Safari WebDriver executable '{driverPath}'.");
        }

        _ = LogOutputAsync(process.StandardOutput, logger, LogLevel.Trace, process.Id);
        _ = LogOutputAsync(process.StandardError, logger, LogLevel.Debug, process.Id);

        return new SafariDriverProcess(
            process,
            new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"));
    }

    private static int GetAvailableLoopbackPort()
    {
        // safaridriver does not document a "bind to port 0" mode, so pick a currently-free loopback port and let the
        // session startup retry if another process takes it before safaridriver binds.
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task LogOutputAsync(StreamReader reader, ILogger logger, LogLevel logLevel, int processId)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                logger.Log(logLevel, "safaridriver ({ProcessId}): {Line}", processId, line);
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class SafariDriverProcess(Process process, Uri webDriverEndpoint) : ISafariDriverProcess
    {
        private readonly Process _process = process;
        private readonly Task<BrowserLogsProcessResult> _processTask = WaitForProcessAsync(process);
        private int _disposed;

        public int ProcessId => _process.Id;

        public Uri WebDriverEndpoint { get; } = webDriverEndpoint;

        public Task<BrowserLogsProcessResult> ProcessTask => _processTask;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            try
            {
                using var exitCts = new CancellationTokenSource(s_processExitTimeout);
                await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static async Task<BrowserLogsProcessResult> WaitForProcessAsync(Process process)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return new BrowserLogsProcessResult(process.ExitCode);
        }
    }
}
