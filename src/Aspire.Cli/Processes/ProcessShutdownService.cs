// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Processes;

/// <summary>
/// Coordinates graceful process shutdown requests, termination monitoring, and force-kill fallback.
/// </summary>
internal sealed class ProcessShutdownService(
    ILayoutDiscovery layoutDiscovery,
    IBundleService bundleService,
    LayoutProcessRunner layoutProcessRunner,
    CliExecutionContext executionContext,
    ILogger<ProcessShutdownService> logger,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan s_processTerminationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_processTerminationPollInterval = TimeSpan.FromMilliseconds(250);

    public Task<bool> StopProcessTreeAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken)
    {
        return StopProcessesAsync(
            [new ProcessTarget(pid, startTime)],
            token => RequestProcessTreeGracefulShutdownAsync(pid, startTime, includeStartTimeForDcp, token),
            cancellationToken);
    }

    public async Task<bool> StopAppHostAsync(
        AppHostInformation? appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken)
    {
        if (appHostInfo is null)
        {
            return requestRpcStopAsync is not null && await TryRequestRpcStopAsync(requestRpcStopAsync, cancellationToken).ConfigureAwait(false);
        }

        var appHostProcess = new ProcessTarget(appHostInfo.ProcessId, appHostInfo.StartedAt);
        var processesToForceKill = new List<ProcessTarget> { appHostProcess };
        if (appHostInfo.CliProcessId is int cliPid)
        {
            // The CLI process is a shutdown handle, not the success condition. On Unix it can remain
            // observable until its parent reaps it after the AppHost has already stopped.
            processesToForceKill.Add(new ProcessTarget(cliPid, appHostInfo.CliStartedAt));
        }

        return await StopProcessesAsync(
            processesToMonitor: [appHostProcess],
            processesToForceKill,
            token => RequestAppHostGracefulShutdownAsync(appHostInfo, requestRpcStopAsync, token),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> StopProcessesAsync(
        IReadOnlyCollection<ProcessTarget> processesToMonitorAndKill,
        Func<CancellationToken, Task<bool>> requestGracefulShutdownAsync,
        CancellationToken cancellationToken)
    {
        return await StopProcessesAsync(
            processesToMonitorAndKill,
            processesToMonitorAndKill,
            requestGracefulShutdownAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> StopProcessesAsync(
        IReadOnlyCollection<ProcessTarget> processesToMonitor,
        IReadOnlyCollection<ProcessTarget> processesToForceKill,
        Func<CancellationToken, Task<bool>> requestGracefulShutdownAsync,
        CancellationToken cancellationToken)
    {
        var gracefulShutdownRequested = await TryRequestGracefulShutdownAsync(requestGracefulShutdownAsync, cancellationToken).ConfigureAwait(false);
        if (gracefulShutdownRequested && await MonitorProcessesForTerminationAsync(processesToMonitor, cancellationToken).ConfigureAwait(false))
        {
            ForceKillRemainingProcesses(processesToForceKill.Except(processesToMonitor), afterTimeout: false);
            return true;
        }

        ForceKillRemainingProcesses(processesToForceKill, afterTimeout: true);

        return await MonitorProcessesForTerminationAsync(processesToMonitor, cancellationToken).ConfigureAwait(false);
    }

    private void ForceKillRemainingProcesses(IEnumerable<ProcessTarget> processes, bool afterTimeout)
    {
        // On Unix the AppHost's process tree does not include DCP (it is launched in its own
        // session/process group), so a tree kill of the AppHost is safe: DCP will detect the
        // AppHost exiting and gracefully tear down its own children. The same applies to the
        // launcher CLI handle - any leftover `dotnet run` / AppHost descendants get cleaned up.
        // On Windows DCP is an in-tree descendant of the AppHost, so we must single-process-kill
        // here and rely on the graceful DCP `stop-process-tree` path for orderly resource cleanup.
        var killEntireProcessTree = !OperatingSystem.IsWindows();

        foreach (var process in processes.Distinct())
        {
            if (afterTimeout)
            {
                logger.LogWarning("Process {Pid} did not stop gracefully within timeout. Forcing process to terminate.", process.Pid);
            }
            else
            {
                logger.LogDebug("Forcing remaining shutdown handle process {Pid} to terminate.", process.Pid);
            }

            ProcessSignaler.ForceKill(process.Pid, process.StartTime, logger, killEntireProcessTree);
        }
    }

    private async Task<bool> RequestAppHostGracefulShutdownAsync(
        AppHostInformation appHostInfo,
        Func<CancellationToken, Task<bool>>? requestRpcStopAsync,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Signal the AppHost directly with SIGTERM. The AppHost catches SIGTERM via
            // Microsoft.Extensions.Hosting and invokes IHostApplicationLifetime.StopApplication,
            // which gives DCP and all in-process resources the orderly shutdown they expect.
            //
            // Routing the graceful signal through the launcher CLI (CliProcessId) cascades via
            // `dotnet run`'s child kill. That walk depends on the AppHost being visible in /proc
            // as a descendant of the `dotnet` process at the moment of the walk, and on the
            // AppHost being reaped by its parent rather than orphaned. When either of those races
            // misfires the AppHost is left running (or lingering as a zombie reparented to PID 1)
            // and the StopCommand monitor then times out reporting "Failed to stop apphost".
            // Targeting the AppHost PID directly avoids the cascade entirely.
            logger.LogDebug("Sending graceful shutdown to AppHost PID {Pid}", appHostInfo.ProcessId);
            return await RequestProcessTreeGracefulShutdownAsync(appHostInfo.ProcessId, appHostInfo.StartedAt, includeStartTimeForDcp: false, cancellationToken).ConfigureAwait(false);
        }

        // On Windows DCP is an in-tree descendant of the AppHost, so we cannot tree-kill the
        // AppHost without also taking DCP down. Instead, run the graceful shutdown against the
        // launcher CLI's process tree (DCP performs `stop-process-tree --skip-descendants`),
        // which signals the AppHost via DCP without disrupting the descendant cleanup DCP is
        // responsible for.
        if (appHostInfo.CliProcessId is int cliPid)
        {
            logger.LogDebug("Requesting AppHost process tree shutdown via root CLI PID {Pid}", cliPid);
            // CliStartedAt is recorded with second-level precision, so validate it locally with tolerance
            // instead of passing it to DCP's millisecond-precision process-start-time option.
            return await RequestProcessTreeGracefulShutdownAsync(cliPid, appHostInfo.CliStartedAt, includeStartTimeForDcp: false, cancellationToken).ConfigureAwait(false);
        }

        if (requestRpcStopAsync is not null && await TryRequestRpcStopAsync(requestRpcStopAsync, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        logger.LogDebug("RPC stop not available, requesting shutdown via AppHost PID {Pid}", appHostInfo.ProcessId);
        return await RequestProcessTreeGracefulShutdownAsync(appHostInfo.ProcessId, appHostInfo.StartedAt, includeStartTimeForDcp: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRequestGracefulShutdownAsync(
        Func<CancellationToken, Task<bool>> requestGracefulShutdownAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            return await requestGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to request graceful process shutdown.");
            return false;
        }
    }

    private async Task<bool> TryRequestRpcStopAsync(Func<CancellationToken, Task<bool>> requestRpcStopAsync, CancellationToken cancellationToken)
    {
        try
        {
            return await requestRpcStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send stop signal via RPC");
            return false;
        }
    }

    private async Task<bool> RequestProcessTreeGracefulShutdownAsync(
        int pid,
        DateTimeOffset? startTime,
        bool includeStartTimeForDcp,
        CancellationToken cancellationToken)
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, startTime, logger);
        if (process is null)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return await TryStopProcessTreeWithDcpAsync(pid, startTime, includeStartTimeForDcp, cancellationToken).ConfigureAwait(false);
        }

        logger.LogDebug("Sending stop signal to process {Pid}", pid);
        ProcessSignaler.RequestGracefulShutdown(pid, startTime, logger);
        return true;
    }

    internal async Task<bool> TryStopProcessTreeWithDcpAsync(int pid, DateTimeOffset? startTime, bool includeStartTime, CancellationToken cancellationToken)
    {
        using var process = ProcessSignaler.TryGetRunningProcess(pid, startTime, logger);
        if (process is null)
        {
            return true;
        }

        using var layoutLease = await bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "dcp-stop-process-tree", cancellationToken).ConfigureAwait(false);
        var dcpDirectory = layoutLease?.Layout.GetDcpPath() ??
            layoutDiscovery.GetComponentPath(LayoutComponent.Dcp, executionContext.WorkingDirectory.FullName);
        if (dcpDirectory is null)
        {
            logger.LogWarning("Could not find DCP in the Aspire layout.");
            return false;
        }

        var dcpPath = BundleDiscovery.GetDcpExecutablePath(dcpDirectory);
        if (!File.Exists(dcpPath))
        {
            logger.LogWarning("Could not find DCP executable at '{DcpPath}'.", dcpPath);
            return false;
        }

        // Ensure we only stop the target process and not all children to allow DCP to avoid accidentally killing the child DCP instance.
        var arguments = new List<string>
        {
            "stop-process-tree",
            "--skip-descendants",
            "--pid",
            pid.ToString(CultureInfo.InvariantCulture)
        };

        if (includeStartTime && startTime is not null)
        {
            arguments.Add("--process-start-time");
            arguments.Add(FormatDcpProcessStartTime(startTime.Value));
        }

        var (exitCode, output, error) = await layoutProcessRunner.RunAsync(
            dcpPath,
            arguments,
            workingDirectory: executionContext.WorkingDirectory.FullName,
            ct: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(output))
        {
            logger.LogDebug("DCP stop-process-tree stdout: {Output}", output.Trim());
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            logger.LogDebug("DCP stop-process-tree stderr: {Error}", error.Trim());
        }

        if (exitCode != 0)
        {
            logger.LogWarning("DCP stop-process-tree exited with code {ExitCode}.", exitCode);
            return false;
        }

        return true;
    }

    private async Task<bool> MonitorProcessesForTerminationAsync(IReadOnlyCollection<ProcessTarget> processes, CancellationToken cancellationToken)
    {
        var startTime = timeProvider.GetUtcNow();
        while (timeProvider.GetUtcNow() - startTime < s_processTerminationTimeout)
        {
            if (processes.All(IsProcessStopped))
            {
                return true;
            }

            await Task.Delay(s_processTerminationPollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private bool IsProcessStopped(ProcessTarget process)
    {
        using var runningProcess = ProcessSignaler.TryGetRunningProcess(process.Pid, process.StartTime, logger);
        return runningProcess is null;
    }

    internal static string FormatDcpProcessStartTime(DateTimeOffset startTime)
    {
        return startTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    internal readonly record struct ProcessTarget(int Pid, DateTimeOffset? StartTime);
}
