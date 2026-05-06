// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
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
                var portStatus = tunnelStatus.Ports?.FirstOrDefault(p => p.PortNumber == portResource.TargetEndpoint.Port);
                portResource.LastKnownStatus = portStatus;
                if (portStatus?.PortUri is null)
                {
                    return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_PortInactive, _tunnelResource.TunnelId, portResource.TargetEndpoint.Port));
                }
            }

            // Get tunnel and port access status
            var tunnelAccessStatus = await _devTunnelClient.GetAccessAsync(_tunnelResource.ResolvedTunnelId, portNumber: null, logger, cancellationToken).ConfigureAwait(false);
            _tunnelResource.LastKnownAccessStatus = tunnelAccessStatus;

            // Get access status for each port
            foreach (var portResource in _tunnelResource.Ports)
            {
                var portAccessStatus = await _devTunnelClient.GetAccessAsync(_tunnelResource.ResolvedTunnelId, portResource.TargetEndpoint.Port, logger, cancellationToken).ConfigureAwait(false);
                portResource.LastKnownAccessStatus = portAccessStatus;
            }

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
#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
