// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Processes;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Utility class for cleaning up AppHost process trees whose launching CLI is no longer running 
/// (including removing their backchannel sockets).
/// </summary>
internal sealed class OrphanedAppHostCollector(
    IAuxiliaryBackchannelMonitor backchannelMonitor,
    IAppHostStopper processShutdownService,
    ILogger<OrphanedAppHostCollector> logger)
{
    /// <summary>
    /// Scans for running AppHosts and stops every one whose launching CLI is no longer alive (best effort).
    /// </summary>
    /// <returns>The number of orphaned AppHosts that were collected.</returns>
    public async Task<int> CollectAsync(CancellationToken cancellationToken)
    {
        List<IAppHostAuxiliaryBackchannel> orphans;
        try
        {
            await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);

            orphans = backchannelMonitor.Connections.Where(IsOrphaned).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Discovering orphans is best effort: a scan or liveness-probe failure must not fail the
            // caller (e.g. `aspire ps`/`aspire stop`), which honors the "(best effort)" contract without
            // requiring every call site to guard. Cancellation still propagates so callers can abort.
            logger.LogDebug(ex, "Failed to scan for orphaned AppHosts.");
            return 0;
        }

        if (orphans.Count == 0)
        {
            return 0;
        }

        var collected = 0;
        foreach (var connection in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var appHostInfo = connection.AppHostInfo;
            try
            {
                var stopped = await processShutdownService.StopAppHostAsync(
                    appHostInfo,
                    connection.StopAppHostAsync,
                    cancellationToken).ConfigureAwait(false);

                if (stopped)
                {
                    // The process is confirmed gone, so the socket's owner is gone and the file is safe
                    // to remove by exact path (mirrors StopCommand). Leaving it behind would have later
                    // commands rediscover a dead AppHost.
                    AppHostHelper.TryDeleteSocketFile(connection.SocketPath, logger);
                    collected++;
                    logger.LogDebug(
                        "Collected orphaned AppHost {AppHostPath} (PID {AppHostPid}); its launching CLI {CliPid} is no longer running.",
                        appHostInfo?.AppHostPath,
                        appHostInfo?.ProcessId,
                        appHostInfo?.CliProcessId);
                }
                else
                {
                    logger.LogDebug(
                        "Failed to collect orphaned AppHost {AppHostPath} (PID {AppHostPid}).",
                        appHostInfo?.AppHostPath,
                        appHostInfo?.ProcessId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Error while collecting orphaned AppHost at {SocketPath}.", connection.SocketPath);
            }
        }

        return collected;
    }

    internal static bool IsOrphaned(IAppHostAuxiliaryBackchannel connection)
    {
        // Only AppHosts launched by a CLI can be attributed to an owner. 
        // Without a CliProcessId we cannot tell whether the AppHost is orphaned, so we leave it alone.
        if (connection.AppHostInfo is not { CliProcessId: int cliPid })
        {
            return false;
        }

        if (connection.AppHostInfo.CliStableStartedAt is { } cliStableStartedAt)
        {
            // Current AppHosts report the launching CLI's start time in Unix milliseconds.
            // It is immune to wall-clock shifts, so a millisecond comparison reliably distinguishes the
            // original launcher from a recycled PID: a mismatch here is trustworthy evidence the launcher is gone.
            return !ProcessStartTimeHelper.IsProcessRunning(cliPid, cliStableStartedAt.ToUnixTimeMilliseconds());
        }

        // Legacy fallback (older AppHost): only ASPIRE_CLI_STARTED is available, which is stamped from Process.StartTime. 
        // On Linux that value drifts across processes after any clock correction (NTP, container suspend/resume), 
        // so a start-time MISMATCH is NOT trustworthy evidence that the PID was reused. 
        // We therefore only treat the AppHost as orphaned when the launching CLI PID is entirely gone, 
        // biasing toward leaving a possible orphan (still guarded by the in-process watchdogs) over tearing down a live one.
        return !ProcessStartTimeHelper.IsProcessRunning(cliPid);
    }
}
