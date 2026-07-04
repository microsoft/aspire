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
        await backchannelMonitor.ScanAsync(cancellationToken).ConfigureAwait(false);

        var orphans = backchannelMonitor.Connections.Where(IsOrphaned).ToList();
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

        if (connection.AppHostInfo.CliStartedAt is { } cliStartedAt)
        {
            // AppHostInfo.CliStartedAt is populated from ASPIRE_CLI_STARTED for backchannel
            // compatibility with released AppHosts. That variable intentionally stays in the
            // Process.StartTime clock domain, so compare it with the legacy verifier instead of
            // the Linux /proc-based stable verifier.
            return !ProcessStartTimeHelper.IsProcessRunningWithRuntimeStartTime(
                cliPid,
                cliStartedAt.ToUnixTimeSeconds(),
                TimeSpan.FromSeconds(1));
        }

        return !ProcessStartTimeHelper.IsProcessRunning(cliPid);
    }
}
