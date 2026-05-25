// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelPortHealthCheck(DevTunnelPortResource portResource) : IHealthCheck
{
    private readonly DevTunnelPortResource _portResource = portResource ?? throw new ArgumentNullException(nameof(portResource));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var tunnelResource = _portResource.DevTunnel;
            var port = await _portResource.GetTunnelPortAsync(cancellationToken).ConfigureAwait(false);
            var tunnelStatus = tunnelResource.LastKnownStatus;
            if (tunnelStatus is null)
            {
                return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelPortUnhealthy_StatusUnknown, port, tunnelResource.TunnelId));
            }

            if (tunnelStatus.HostConnections == 0)
            {
                return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_NoActiveHostConnections, tunnelResource.TunnelId));
            }

            var portStatus = tunnelStatus.Ports?.FirstOrDefault(p => p.PortNumber == port);

            // Check that port is active
            if (portStatus?.PortUri is null)
            {
                return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelUnhealthy_PortInactive, tunnelResource.TunnelId, port));
            }

            return HealthCheckResult.Healthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelPortHealthy, port, tunnelResource.TunnelId));
        }
        catch (Exception ex)
        {
            var tunnelResource = _portResource.DevTunnel;
            var port = _portResource.TargetEndpoint.TargetPort?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            return HealthCheckResult.Unhealthy(string.Format(CultureInfo.CurrentCulture, Resources.MessageStrings.DevTunnelPortUnhealthy_Error, port, tunnelResource.TunnelId, ex.Message), ex);
        }
    }
}
