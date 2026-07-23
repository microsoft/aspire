// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelHealthCheck(
    IDevTunnelClient devTunnelClient,
    LoggedOutNotificationManager loggedOutNotificationManager,
    DevTunnelResource tunnelResource,
    ILogger<DevTunnelHealthCheck> logger) : IHealthCheck
{
    private readonly IDevTunnelClient _devTunnelClient = devTunnelClient ?? throw new ArgumentNullException(nameof(devTunnelClient));

    private readonly LoggedOutNotificationManager _loggedOutNotificationManager = loggedOutNotificationManager ?? throw new ArgumentNullException(nameof(loggedOutNotificationManager));

    private readonly DevTunnelResource _tunnelResource = tunnelResource ?? throw new ArgumentNullException(nameof(tunnelResource));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var tunnelStatus = await _devTunnelClient.GetTunnelAsync(_tunnelResource.ResolvedTunnelId, logger, cancellationToken).ConfigureAwait(false);
            tunnelResource.LastKnownStatus = tunnelStatus;
            if (tunnelStatus.HostConnections == 0)
            {
                return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_NoActiveHostConnections, _tunnelResource.TunnelId));
            }

            // Check that expected ports are active
            foreach (var portResource in _tunnelResource.Ports)
            {
                var tunnelPort = await portResource.GetTunnelPortAsync(cancellationToken).ConfigureAwait(false);
                var portStatus = tunnelStatus.Ports?.FirstOrDefault(p => p.PortNumber == tunnelPort);
                portResource.LastKnownStatus = portStatus;
                if (portStatus?.PortUri is null)
                {
                    return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_PortInactive, _tunnelResource.TunnelId, tunnelPort));
                }
            }

            _ = DevTunnelAccessStatusRefresh.QueueTunnelAndPortRefresh(_devTunnelClient, _tunnelResource, logger, cancellationToken);

            return HealthCheckResult.Healthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelHealthy, _tunnelResource.TunnelId, tunnelStatus.HostConnections, tunnelStatus.Ports?.Count));
        }
        catch (Exception ex)
        {
            tunnelResource.LastKnownStatus = null;

            try
            {
                // Check if the user is still logged in
                var loginStatus = await _devTunnelClient.GetUserLoginStatusAsync(logger, cancellationToken).ConfigureAwait(false);
                if (!loginStatus.IsLoggedIn)
                {
                    _ = Task.Run(() => _loggedOutNotificationManager.NotifyUserLoggedOutAsync(cancellationToken).ConfigureAwait(false));
                }
            }
            catch { } // Ignore errors from login check

            return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_Error, _tunnelResource.TunnelId, ex.Message), ex);
        }
    }
}

internal static class DevTunnelAccessStatusRefresh
{
    private static readonly TimeSpan s_refreshTimeout = TimeSpan.FromSeconds(2);

    public static Task QueueTunnelAndPortRefresh(
        IDevTunnelClient devTunnelClient,
        DevTunnelResource tunnelResource,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(s_refreshTimeout);

                var tunnelAccessStatus = await devTunnelClient.GetAccessAsync(tunnelResource.ResolvedTunnelId, portNumber: null, logger, timeoutCts.Token).ConfigureAwait(false);
                tunnelResource.LastKnownAccessStatus = tunnelAccessStatus;

                foreach (var portResource in tunnelResource.Ports)
                {
                    int? tunnelPort = null;
                    try
                    {
                        tunnelPort = await portResource.GetTunnelPortAsync(timeoutCts.Token).ConfigureAwait(false);
                        var portAccessStatus = await devTunnelClient.GetAccessAsync(tunnelResource.ResolvedTunnelId, tunnelPort, logger, timeoutCts.Token).ConfigureAwait(false);

                        if (portResource.ActiveTunnelPort is null || portResource.ActiveTunnelPort == tunnelPort)
                        {
                            portResource.LastKnownAccessStatus = portAccessStatus;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        ClearPortAccessStatus(portResource, tunnelPort);
                        logger.LogDebug(ex, "Failed to refresh dev tunnel access status for port resource '{PortResource}' on tunnel '{Tunnel}'.", portResource.Name, tunnelResource.TunnelId);
                        if (timeoutCts.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                tunnelResource.LastKnownAccessStatus = null;
                logger.LogDebug(ex, "Failed to refresh dev tunnel access status for tunnel '{Tunnel}'.", tunnelResource.TunnelId);
            }
        }, CancellationToken.None);
    }

    public static Task QueuePortRefresh(
        IDevTunnelClient devTunnelClient,
        DevTunnelPortResource portResource,
        ResourceNotificationService notifications,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || portResource.ActiveTunnelPort is not { } activeTunnelPort)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(s_refreshTimeout);

                var tunnelAccessStatus = await devTunnelClient.GetAccessAsync(portResource.DevTunnel.ResolvedTunnelId, portNumber: null, logger, timeoutCts.Token).ConfigureAwait(false);
                var portAccessStatus = await devTunnelClient.GetAccessAsync(portResource.DevTunnel.ResolvedTunnelId, activeTunnelPort, logger, timeoutCts.Token).ConfigureAwait(false);

                await portResource.PortUpdateLock.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                try
                {
                    if (portResource.ActiveTunnelPort != activeTunnelPort)
                    {
                        return;
                    }

                    portResource.DevTunnel.LastKnownAccessStatus = tunnelAccessStatus;
                    portResource.LastKnownAccessStatus = portAccessStatus;

                    var effectivePolicy = portAccessStatus.LogAnonymousAccessPolicy(logger);
                    await notifications.PublishUpdateAsync(portResource, snapshot =>
                    {
                        if (portResource.ActiveTunnelPort != activeTunnelPort)
                        {
                            return snapshot;
                        }

                        return snapshot with
                        {
                            Properties = [
                                .. snapshot.Properties.Where(p => !string.Equals(p.Name, "Anonymous access", StringComparison.OrdinalIgnoreCase)),
                                new("Anonymous access", effectivePolicy)
                            ]
                        };
                    }).ConfigureAwait(false);
                }
                finally
                {
                    portResource.PortUpdateLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                if (portResource.ActiveTunnelPort == activeTunnelPort)
                {
                    portResource.DevTunnel.LastKnownAccessStatus = null;
                    portResource.LastKnownAccessStatus = null;
                }

                logger.LogDebug(ex, "Failed to refresh dev tunnel access status for port '{Port}' on tunnel '{Tunnel}'.", activeTunnelPort, portResource.DevTunnel.TunnelId);
            }
        }, CancellationToken.None);
    }

    private static void ClearPortAccessStatus(DevTunnelPortResource portResource, int? tunnelPort)
    {
        if (tunnelPort is null || portResource.ActiveTunnelPort is null || portResource.ActiveTunnelPort == tunnelPort)
        {
            portResource.LastKnownAccessStatus = null;
        }
    }
}
