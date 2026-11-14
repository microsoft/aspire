// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Resolves app-model endpoint behavior and allocates ports independently of DCP service rendering.
/// </summary>
internal sealed class DcpEndpointAllocator(
    IOptions<DcpOptions> options,
    PersistentPortAllocator portAllocator,
    ILogger<DcpEndpointAllocator> logger)
{
    public (IResource ModelResource, EndpointAnnotation[] Endpoints)[] PrepareEndpoints(DistributedApplicationModel model)
    {
        logger.LogDebug("Preparing services. Ports randomized: {RandomizePorts}", options.Value.RandomizePorts);

        var producers = model.Resources
            .Select(resource => (ModelResource: resource, Endpoints: resource.Annotations.OfType<EndpointAnnotation>().ToArray()))
            .Where(producer => producer.Endpoints.Length > 0)
            .ToArray();

        // Resolve every endpoint before the allocator excludes fixed ports or reserves dynamic ones.
        foreach (var producer in producers)
        {
            foreach (var endpoint in producer.Endpoints)
            {
                endpoint.SetResolvedIsProxied(GetEffectiveIsProxied(producer.ModelResource, endpoint, options.Value.RandomizePorts));
                DcpModelUtilities.ValidateEndpointPorts(producer.ModelResource, endpoint);
            }
        }

        return producers;
    }

    public int? AllocatePublicPort(IResource resource, EndpointAnnotation endpoint)
    {
        if (!endpoint.IsProxied &&
            DcpModelUtilities.NeedsGeneratedPublicPort(resource, endpoint, randomizePorts: false))
        {
            var publicPort = portAllocator.GetOrAllocatePersistentPort(
                new(resource.Name, endpoint.Name),
                endpoint.Protocol);
            logger.LogDebug("Allocated public port {Port} for proxyless endpoint '{EndpointName}' on resource '{ResourceName}'.", publicPort, endpoint.Name, resource.Name);

            endpoint.Port = publicPort;
            if (!resource.IsContainer())
            {
                endpoint.TargetPort = publicPort;
            }
        }

        if (DcpModelUtilities.TryGetEffectiveFixedPublicPort(resource, endpoint, options.Value.RandomizePorts, out var fixedPublicPort))
        {
            return fixedPublicPort;
        }

        return endpoint.IsProxied && portAllocator.PersistenceEnabled
            ? portAllocator.GetOrAllocatePersistentPort(new(resource.Name, endpoint.Name), endpoint.Protocol)
            : null;
    }

    public int? AllocateExecutableTargetPort(EndpointAnnotation endpoint)
    {
        // DCP probes and releases dynamic producer ports before starting the process. Use Aspire's
        // non-ephemeral range so outbound connections cannot claim the port during that gap.
        return endpoint.IsProxied && EndpointAnnotation.NormalizePort(endpoint.TargetPort) is null
            ? portAllocator.AllocateTransientPort(endpoint.Protocol)
            : endpoint.TargetPort;
    }

    public int? AllocateExecutableTargetPort(IResource resource, EndpointAnnotation endpoint)
    {
        return resource.HasPersistentLifetime() && endpoint.IsProxied && EndpointAnnotation.NormalizePort(endpoint.TargetPort) is null
            ? portAllocator.GetOrAllocatePersistentPort(PersistentPortAllocator.GetTargetPortKey(resource, endpoint), endpoint.Protocol)
            : AllocateExecutableTargetPort(endpoint);
    }

    internal static bool TryAllocateLocalhostEndpoint(
        EndpointAnnotation endpoint,
        string serviceName,
        string? address,
        int? port,
        bool hasCompleteAddress,
        string targetPortExpression,
        bool allowPending)
    {
        if (endpoint.AllocatedEndpoint is not null)
        {
            return true;
        }

        if (!hasCompleteAddress && endpoint.IsProxied)
        {
            if (allowPending)
            {
                return false;
            }

            throw new InvalidDataException($"Service {serviceName} should have valid address at this point");
        }

        if (!endpoint.IsProxied && port is null)
        {
            if (allowPending)
            {
                return false;
            }

            throw new InvalidOperationException($"Service '{serviceName}' needs to specify a port for endpoint '{endpoint.Name}' since it isn't using a proxy.");
        }

        if (port is null || string.IsNullOrEmpty(address))
        {
            if (allowPending)
            {
                return false;
            }

            throw new InvalidDataException($"Service {serviceName} should have valid address at this point");
        }

        var (targetHost, bindingMode) = DcpModelUtilities.NormalizeTargetHost(endpoint.TargetHost);
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(
            endpoint, targetHost, port.Value, bindingMode, targetPortExpression,
            KnownNetworkIdentifiers.LocalhostNetwork);

        return true;
    }

    private static bool GetEffectiveIsProxied(IResource resource, EndpointAnnotation endpoint, bool randomizePorts)
    {
        if (!resource.SupportsProxy())
        {
            return false;
        }

        if (endpoint.IsExplicitlyProxied is bool isProxied)
        {
            return isProxied;
        }

        return randomizePorts || !resource.HasPersistentLifetime();
    }
}
