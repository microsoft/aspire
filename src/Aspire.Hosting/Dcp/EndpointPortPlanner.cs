// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dcp;

// Shared port policy for listeners that start before workloads and for DCP service creation.
// Planning reads the model without resolving or validating its endpoints; DCP owns that phase.
internal static class EndpointPortPlanner
{
    internal static void ReservePorts(
        DistributedApplicationModel model,
        DcpOptions options,
        IConfiguration configuration,
        ProxylessEndpointPortAllocator allocator,
        ILogger logger)
    {
        // Exclude ports the model already needs before an early listener or workload allocates one.
        foreach (var resource in model.Resources)
        {
            foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>())
            {
                if (TryGetEffectiveFixedPublicPort(resource, endpoint, options.RandomizePorts, out var fixedPublicPort))
                {
                    allocator.ExcludePort(fixedPublicPort);
                }

                if (TryGetPersistedProxylessEndpointPort(resource, endpoint, options.RandomizePorts, configuration, logger) is int persistedPort)
                {
                    allocator.ExcludePort(persistedPort);
                }

                if (resource is IComputeResource &&
                    !resource.IsContainer() &&
                    EndpointAnnotation.NormalizePort(endpoint.GetTargetPort(GetEffectiveIsProxied(resource, endpoint, options.RandomizePorts))) is int fixedTargetPort)
                {
                    allocator.ExcludePort(fixedTargetPort);
                }
            }
        }
    }

    internal static bool GetEffectiveIsProxied(IResource resource, EndpointAnnotation endpoint, bool randomizePorts)
    {
        if (!resource.SupportsProxy())
        {
            return false;
        }

        if (endpoint.IsExplicitlyProxied is bool isProxied)
        {
            return isProxied;
        }

        if (randomizePorts)
        {
            return true;
        }

        return !resource.HasPersistentLifetime();
    }

    /// <summary>
    /// Determines whether an endpoint definition has a fixed public port DCP should reserve or pre-exclude.
    /// </summary>
    /// <remarks>
    /// Use this when deciding whether DCP should bind a service to a known public port. Proxied endpoints
    /// with randomized ports deliberately do not report a fixed port so DCP can allocate the public port
    /// instead of reserving the configured value.
    /// Port 0 requests dynamic allocation and therefore does not count as a fixed public port.
    /// Container endpoint definitions keep the public host port separate from the target container port, so
    /// only an explicitly specified public port counts as fixed. Executable endpoint definitions use the same
    /// port value for the process and the public endpoint, so the effective public port can come from either
    /// the endpoint port or target port.
    /// </remarks>
    internal static bool TryGetEffectiveFixedPublicPort(IResource resource, EndpointAnnotation endpoint, bool randomizePorts, out int publicPort)
    {
        var effectivePublicPort = EndpointAnnotation.NormalizePort(resource.IsContainer() ? endpoint.SpecifiedPort : endpoint.GetPort(GetEffectiveIsProxied(resource, endpoint, randomizePorts)));

        // When port randomization is enabled, proxied endpoints intentionally ignore the defined public
        // port so DCP can allocate one dynamically instead.
        if (randomizePorts && GetEffectiveIsProxied(resource, endpoint, randomizePorts) && effectivePublicPort is not null)
        {
            publicPort = default;
            return false;
        }

        if (effectivePublicPort is int fixedPublicPort)
        {
            publicPort = fixedPublicPort;
            return true;
        }

        publicPort = default;
        return false;
    }

    internal static bool NeedsPublicPort(IResource resource, EndpointAnnotation endpoint, bool randomizePorts)
    {
        // DCP can allocate a port only for resources it launches as workloads. This includes compute
        // resources and annotation-backed containers; integration-owned endpoints publish their own addresses.
        return (resource is IComputeResource || resource.IsContainer()) &&
            !GetEffectiveIsProxied(resource, endpoint, randomizePorts) &&
            !TryGetEffectiveFixedPublicPort(resource, endpoint, randomizePorts, out _);
    }

    internal static int? TryGetPersistedProxylessEndpointPort(IResource resource, EndpointAnnotation endpoint, bool randomizePorts, IConfiguration configuration, ILogger logger)
    {
        if (randomizePorts ||
            !resource.HasPersistentLifetime() ||
            !NeedsPublicPort(resource, endpoint, randomizePorts))
        {
            return null;
        }

        var configuredPort = configuration[GetPersistedProxylessEndpointPortKey(resource, endpoint)];
        if (configuredPort is null)
        {
            return null;
        }

        if (int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) &&
            PortRange.IsValidPort(port))
        {
            return port;
        }

        logger.LogDebug("Ignoring invalid persisted public port value '{Port}' for proxyless endpoint '{EndpointName}' on persistent resource '{ResourceName}'.", configuredPort, endpoint.Name, resource.Name);
        return null;
    }

    internal static string GetPersistedProxylessEndpointPortKey(IResource resource, EndpointAnnotation endpoint)
    {
        // Schema suggested by https://github.com/microsoft/aspire/issues/13597:
        // Resources:<resource-name>:<endpoint-name>:port
        return $"Resources:{resource.Name}:{endpoint.Name}:port";
    }
}
