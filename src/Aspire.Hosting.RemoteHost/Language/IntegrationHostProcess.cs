// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost.Language;

/// <summary>
/// Owns an integration supervisor's process scope and drains its diagnostic streams.
/// </summary>
internal sealed partial class IntegrationHostProcess : IAsyncDisposable
{
    private static readonly TimeSpan s_exitTimeout = TimeSpan.FromSeconds(5);
    private readonly Process _process;
    private readonly ILogger _logger;
    private readonly string _packageName;
    private readonly CancellationTokenSource _readersCancellation = new();
    private readonly Task _readers;
    private readonly int _processId;

    private IntegrationHostProcess(Process process, ILogger logger, string packageName)
    {
        _process = process;
        _processId = process.Id;
        _logger = logger;
        _packageName = packageName;
        Exit = WaitForExitAsync();
        _readers = Task.WhenAll(
            ReadAsync(process.StandardOutput, LogLevel.Information),
            ReadAsync(process.StandardError, LogLevel.Warning));
    }

    public int ProcessId => _processId;

    public Task<int> Exit { get; }

    public static IntegrationHostProcess Start(ProcessStartInfo startInfo, ILogger logger, string packageName)
    {
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start integration host supervisor for '{packageName}'.");

        return new IntegrationHostProcess(process, logger, packageName);
    }

    private async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);

        return _process.ExitCode;
    }

    private async Task ReadAsync(StreamReader reader, LogLevel level)
    {
        try
        {
            while (await reader.ReadLineAsync(_readersCancellation.Token).ConfigureAwait(false) is { } line)
            {
                _logger.Log(level, "IntegrationHost[{Name}]: {Line}", _packageName, line);
            }
        }
        catch (OperationCanceledException) when (_readersCancellation.IsCancellationRequested)
        {
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read diagnostics from integration host '{Name}'.", _packageName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                await Exit.WaitAsync(s_exitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Integration host '{Name}' did not stop gracefully; terminating its process scope.", _packageName);
            }

            if (!OperatingSystem.IsWindows())
            {
                KillGroup(_processId);
            }
            // If Unix startup never reached setsid, no group exists yet. There cannot
            // be a runtime child before containment, but still reap the guardian.
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) when (_process.HasExited)
                {
                }
            }

            await Exit.WaitAsync(s_exitTimeout).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                // The guardian can establish its group between the first group
                // signal and root termination. Signal again after reaping it so
                // descendants spawned in that startup window cannot survive.
                KillGroup(_processId);
                using var groupExitCancellation = new CancellationTokenSource(s_exitTimeout);
                while (GroupExists(_processId))
                {
                    await Task.Delay(25, groupExitCancellation.Token).ConfigureAwait(false);
                }
            }
            try
            {
                await _readers.WaitAsync(s_exitTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Integration host '{Name}' left its diagnostic streams open after termination.", _packageName);
                _readersCancellation.Cancel();
                await _readers.ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up integration host '{Name}' (supervisor PID {Pid}).", _packageName, _processId);
            throw;
        }
        finally
        {
            _readersCancellation.Cancel();
            _readersCancellation.Dispose();
            _process.Dispose();
        }
    }

    internal static void KillGroup(int processGroupId)
    {
        // A negative PID targets the entire POSIX process group, including members
        // whose original parent has exited. ESRCH means the scope is already gone.
        // https://pubs.opengroup.org/onlinepubs/9799919799/functions/kill.html
        if (kill(-processGroupId, 9) != 0 && Marshal.GetLastPInvokeError() is not 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not terminate integration host process group {processGroupId}.");
        }
    }

    private static bool GroupExists(int processGroupId)
    {
        if (kill(-processGroupId, 0) == 0)
        {
            return true;
        }
        var error = Marshal.GetLastPInvokeError();
        // Signal zero still performs permission checks. EPERM establishes that the
        // group exists; on macOS it can be transient while orphaned members are reaped.
        // Keep waiting rather than mistaking lack of permission for completed cleanup.
        if (error == 1)
        {
            return true;
        }
        if (error == 3)
        {
            return false;
        }

        throw new Win32Exception(error, $"Could not verify termination of integration host process group {processGroupId}.");
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
