// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Static utility methods for working with the DCP model.
/// </summary>
internal static class DcpModelUtilities
{
    /// <summary>
    /// Determines whether DCP object creation should be deferred until an explicit manual start.
    /// </summary>
    internal static bool ShouldDeferCreateForExplicitStart(IResource modelResource, bool? start)
    {
        // Explicit-start, non-persistent resources use manual snapshots for dashboard visibility.
        // Do not create corresponding DCP objects until the manual start path flips Spec.Start=true; creation
        // evaluates callbacks that can prompt for input or depend on start-time state.
        return start == false &&
            modelResource.TryGetLastAnnotation<ExplicitStartupAnnotation>(out _) &&
            modelResource.GetLifetimeType() != Lifetime.Persistent;
    }

    /// <summary>
    /// Examines the Aspire resource annotations and adds equivalent ServiceProducerAnnotations to the corresponding DCP resource.
    /// </summary>
    internal static void AddServicesProducedInfo<TDcpResource>(
        RenderedModelResource<TDcpResource> appResource,
        IEnumerable<IAppResource> appResources)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        var modelResource = appResource.ModelResource;
        var modelResourceName = modelResource.Name ?? "(unknown)";

        var servicesProduced = appResources.OfType<ServiceWithModelResource>().Where(r => r.ModelResource == modelResource);
        foreach (var sp in servicesProduced)
        {
            var ea = sp.EndpointAnnotation;

            if (modelResource.IsContainer())
            {
                if (ea.TargetPort is null)
                {
                    throw new InvalidOperationException($"The endpoint '{ea.Name}' for container resource '{modelResourceName}' must specify the {nameof(EndpointAnnotation.TargetPort)} value");
                }
            }
            else if (!ea.IsProxied)
            {
                if (HasMultipleReplicas(appResource.DcpResource))
                {
                    throw new InvalidOperationException($"Resource '{modelResourceName}' uses multiple replicas and a proxy-less endpoint '{ea.Name}'. These features do not work together.");
                }

                if (ea.Port is int && ea.Port != ea.TargetPort)
                {
                    throw new InvalidOperationException($"The endpoint '{ea.Name}' for resource '{modelResourceName}' is not using a proxy, and it has a value of {nameof(EndpointAnnotation.Port)} property that is different from the value of {nameof(EndpointAnnotation.TargetPort)} property. For proxy-less endpoints they must match.");
                }
            }
            else
            {
                Debug.Assert(ea.IsProxied);

                if (ea.TargetPort is int && ea.Port is int && ea.TargetPort == ea.Port)
                {
                    throw new InvalidOperationException(
                        $"The endpoint '{ea.Name}' for resource '{modelResourceName}' requested a proxy ({nameof(ea.IsProxied)} is true). Non-container resources cannot be proxied when both {nameof(ea.TargetPort)} and {nameof(ea.Port)} are specified with the same value.");
                }

                if (HasMultipleReplicas(appResource.DcpResource) && ea.TargetPort is int)
                {
                    throw new InvalidOperationException(
                        $"Resource '{modelResourceName}' can have multiple replicas, and it uses endpoint '{ea.Name}' that has {nameof(ea.TargetPort)} property set. Each replica must have a unique port; setting {nameof(ea.TargetPort)} is not allowed.");
                }
            }

            var spAnn = new ServiceProducerAnnotation(sp.Service.Metadata.Name);
            (spAnn.Address, _) = NormalizeTargetHost(ea.TargetHost);
            spAnn.Port = ea.TargetPort;
            appResource.DcpResource.AnnotateAsObjectList(CustomResource.ServiceProducerAnnotation, spAnn);
            appResource.ServicesProduced.Add(sp);
        }

        static bool HasMultipleReplicas(CustomResource resource)
        {
            if (resource is Executable exe && exe.Metadata.Annotations.TryGetValue(CustomResource.ResourceReplicaCount, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var replicas) && replicas > 1)
            {
                return true;
            }
            return false;
        }
    }

    internal static void AddWorkloadAllocatedEndpoints<TDcpResource>(
        IEnumerable<RenderedModelResource<TDcpResource>> resources,
        bool enableAspireContainerTunnel,
        string containerHostName)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        foreach (var res in resources)
        {
            TryAddWorkloadAllocatedEndpoints(res, enableAspireContainerTunnel, containerHostName);
        }
    }

    internal static bool TryAddWorkloadAllocatedEndpoints<TDcpResource>(
        RenderedModelResource<TDcpResource> resource,
        bool enableAspireContainerTunnel,
        string containerHostName)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        foreach (var sp in resource.ServicesProduced)
        {
            if (TryAddLocalhostAllocatedEndpoint(sp, allowPending: false))
            {
                AddContainerNetworkAllocatedEndpoint(resource, sp);
                AddExecutableContainerNetworkAllocatedEndpoint(resource, sp, enableAspireContainerTunnel, containerHostName);
            }
        }

        return AreResourceEndpointsAllocated(resource.ModelResource);
    }

    internal static void ApplyServiceAddressToEndpoint(Service observedService, IEnumerable<IAppResource> appResources)
    {
        var serviceResource = appResources.OfType<ServiceWithModelResource>()
            .FirstOrDefault(swr => string.Equals(swr.DcpResource.Metadata.Name, observedService.Metadata.Name, StringComparison.Ordinal));

        if (serviceResource is null)
        {
            return;
        }

        serviceResource.Service.ApplyAddressInfoFrom(observedService);
        if (!TryAddLocalhostAllocatedEndpoint(serviceResource, allowPending: true))
        {
            return;
        }

        foreach (var containerResource in appResources.OfType<RenderedModelResource<Container>>()
            .Where(resource => ReferenceEquals(resource.ModelResource, serviceResource.ModelResource)))
        {
            AddContainerNetworkAllocatedEndpoint(containerResource, serviceResource);
        }
    }

    private static bool TryAddLocalhostAllocatedEndpoint(ServiceWithModelResource sp, bool allowPending, int? fallbackPort = null)
    {
        var svc = sp.DcpResource;
        var allocatedPort = svc.AllocatedPort ?? fallbackPort;

        if (sp.EndpointAnnotation.AllocatedEndpoint is not null)
        {
            return true;
        }

        if (!svc.HasCompleteAddress && sp.EndpointAnnotation.IsProxied)
        {
            if (allowPending)
            {
                return false;
            }

            // This should never happen; if it does, we have a bug without a workaround for the user.
            // We should have waited for the service to have a complete address before getting here.
            throw new InvalidDataException($"Service {svc.Metadata.Name} should have valid address at this point");
        }

        if (!sp.EndpointAnnotation.IsProxied && allocatedPort is null)
        {
            if (allowPending)
            {
                return false;
            }

            throw new InvalidOperationException($"Service '{svc.Metadata.Name}' needs to specify a port for endpoint '{sp.EndpointAnnotation.Name}' since it isn't using a proxy.");
        }

        if (allocatedPort is null || string.IsNullOrEmpty(svc.AllocatedAddress))
        {
            if (allowPending)
            {
                return false;
            }

            throw new InvalidDataException($"Service {svc.Metadata.Name} should have valid address at this point");
        }

        var (targetHost, bindingMode) = NormalizeTargetHost(sp.EndpointAnnotation.TargetHost);

        sp.EndpointAnnotation.AllocatedEndpoint = new AllocatedEndpoint(
            sp.EndpointAnnotation,
            targetHost,
            allocatedPort.Value,
            bindingMode,
            targetPortExpression: $$$"""{{- portForServing "{{{svc.Metadata.Name}}}" -}}""",
            KnownNetworkIdentifiers.LocalhostNetwork);

        return true;
    }

    private static void AddContainerNetworkAllocatedEndpoint<TDcpResource>(RenderedModelResource<TDcpResource> resource, ServiceWithModelResource sp)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        if (resource.DcpResource is not Container ctr || ctr.Spec.Networks is null)
        {
            return;
        }

        // Once container networks are fully supported, this should allocate endpoints on those networks.
        var containerNetwork = ctr.Spec.Networks.FirstOrDefault(n => n.Name == KnownNetworkIdentifiers.DefaultAspireContainerNetwork.Value);

        if (containerNetwork is null)
        {
            return;
        }

        var port = sp.EndpointAnnotation.TargetPort!;

        var allocatedEndpoint = new AllocatedEndpoint(
            sp.EndpointAnnotation,
            $"{sp.ModelResource.Name}.dev.internal",
            (int)port,
            EndpointBindingMode.SingleAddress,
            targetPortExpression: $$$"""{{- portForServing "{{{sp.DcpResource.Metadata.Name}}}" -}}""",
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork
        );
        sp.EndpointAnnotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(allocatedEndpoint.NetworkID, allocatedEndpoint);
    }

    private static void AddExecutableContainerNetworkAllocatedEndpoint<TDcpResource>(RenderedModelResource<TDcpResource> resource, ServiceWithModelResource sp, bool enableAspireContainerTunnel, string containerHostName)
        where TDcpResource : CustomResource, IKubernetesStaticMetadata
    {
        if (resource.DcpResource is not Executable || enableAspireContainerTunnel)
        {
            return;
        }

        // If we are not using the tunnel, we can project Executable endpoints into container networks via ContainerHostName.
        // This really only works for Docker Desktop, but it is useful for testing too.
        var allocatedEndpoint = new AllocatedEndpoint(
            sp.EndpointAnnotation,
            containerHostName,
            (int)sp.DcpResource.AllocatedPort!,
            EndpointBindingMode.SingleAddress,
            targetPortExpression: $$$"""{{- portForServing "{{{sp.DcpResource.Metadata.Name}}}" -}}""",
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork
        );
        sp.EndpointAnnotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(KnownNetworkIdentifiers.DefaultAspireContainerNetwork, allocatedEndpoint);
    }

    internal static bool AreResourceEndpointsAllocated(IResource resource)
    {
        return !resource.TryGetEndpoints(out var endpoints) || endpoints.All(e => e.AllocatedEndpoint is not null);
    }

    internal static void AddContainerTunnelAllocatedEndpoints(
        IEnumerable<IResource> affectedResources,
        DcpAppResourceStore allAppResources,
        string containerHostName)
    {
        foreach (var res in affectedResources)
        {
            // If there are any additional services that are not directly produced by this resource,
            // but leverage its endpoints via container tunnel, we want to add allocated endpoint info for them as well.

            var tunnelServices = allAppResources.Get().OfType<AppResource<Service>>().Select(r => (
                Service: r.DcpResource,
                ResourceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ResourceNameAnnotation, out var resourceName) == true ? resourceName : null,
                EndpointName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.EndpointNameAnnotation, out var endpointName) == true ? endpointName : null,
                TunnelInstanceName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ContainerTunnelInstanceName, out var tunnelInstanceName) == true ? tunnelInstanceName : null,
                ContainerNetworkName: r.DcpResource.Metadata.Annotations?.TryGetValue(CustomResource.ContainerNetworkAnnotation, out var containerNetworkName) == true ? containerNetworkName : null
            ))
            .Where(ts =>
                ts.Service is not null &&
                string.Equals(ts.ResourceName, res.Name, StringComparisons.ResourceName) &&
                !string.IsNullOrEmpty(ts.EndpointName) &&
                !string.IsNullOrEmpty(ts.ContainerNetworkName)
            );

            foreach (var ts in tunnelServices)
            {
                if (!TryGetEndpoint(res, ts.EndpointName, out var endpoint))
                {
                    throw new InvalidDataException($"Service '{ts.Service!.Metadata.Name}' refers to endpoint '{ts.EndpointName}' that does not exist");
                }

                if (ts.Service?.HasCompleteAddress is not true)
                {
                    // This should never happen; if it does, we have a bug without a workaround for the user.
                    throw new InvalidDataException($"Container tunnel service {ts.Service?.Metadata.Name} should have valid address at this point");
                }

                var serverSvc = allAppResources.Get().OfType<ServiceWithModelResource>().FirstOrDefault(swr =>
                    string.Equals(swr.ModelResource.Name, ts.ResourceName, StringComparisons.ResourceName) &&
                    string.Equals(swr.EndpointAnnotation.Name, endpoint.Name, StringComparisons.EndpointAnnotationName)
                );
                if (serverSvc is null)
                {
                    // Should never happen -- we should have created a Service for every endpoint exposed from a resource.
                    throw new InvalidDataException($"The '{endpoint.Name}' on resource '{ts.ResourceName}' should have an associated DCP Service resource already set up");
                }

                var networkId = new NetworkIdentifier(ts.ContainerNetworkName!);
                var address = string.IsNullOrEmpty(ts.TunnelInstanceName) ? containerHostName : KnownHostNames.DefaultContainerTunnelHostName;
                var port = (int)ts.Service!.AllocatedPort!;

                var tunnelAllocatedEndpoint = new AllocatedEndpoint(
                    endpoint,
                    address,
                    port,
                    EndpointBindingMode.SingleAddress,
                    targetPortExpression: $$$"""{{- portForServing "{{{ts.Service.Metadata.Name}}}" -}}""",
                    networkId
                );
                endpoint.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(networkId, tunnelAllocatedEndpoint);
            }
        }
    }

    private static bool TryGetEndpoint(IResource resource, string? endpointName, [NotNullWhen(true)] out EndpointAnnotation? endpoint)
    {
        endpoint = null;
        if (resource.TryGetAnnotationsOfType<EndpointAnnotation>(out var endpoints))
        {
            endpoint = endpoints.FirstOrDefault(e => string.Equals(e.Name, endpointName, StringComparisons.EndpointAnnotationName));
        }

        return endpoint is not null;
    }

    /// <summary>
    /// Normalize the target host to a tuple of (address, binding mode) to a single valid address for
    /// service discovery purposes. A user may have configured an endpoint target host that isn't itself
    /// a valid IP address or hostname that can be resolved by other services or clients. For example,
    /// 0.0.0.0 is considered to mean that the service should bind to all IPv4 addresses. When the target
    /// host indicates that the service should bind to all IPv4 or IPv6 addresses, we instead return
    /// "localhost" as the address as that is a valid address for the .NET dev certificate. The binding mode
    /// is metadata that indicates whether an endpoint is bound to a single address or some set of multiple
    /// addresses on the system.
    /// </summary>
    /// <param name="targetHost">The target host from an EndpointAnnotation</param>
    /// <returns>A tuple of (address, binding mode).</returns>
    internal static (string, EndpointBindingMode) NormalizeTargetHost(string targetHost)
    {
        return targetHost switch
        {
            null or "" => (KnownHostNames.Localhost, EndpointBindingMode.SingleAddress), // Default is localhost
            var s when EndpointHostHelpers.IsLocalhostOrLocalhostTld(s) => (KnownHostNames.Localhost, EndpointBindingMode.SingleAddress), // Explicitly set to localhost or .localhost subdomain

            var s when IPAddress.TryParse(s, out var ipAddress) => ipAddress switch // The host is an IP address
            {
                var ip when IPAddress.Any.Equals(ip) => (KnownHostNames.Localhost, EndpointBindingMode.IPv4AnyAddresses), // 0.0.0.0 (IPv4 all addresses)
                var ip when IPAddress.IPv6Any.Equals(ip) => (KnownHostNames.Localhost, EndpointBindingMode.IPv6AnyAddresses), // :: (IPv6 all addresses)
                _ => (s, EndpointBindingMode.SingleAddress), // Any other IP address is returned as-is as that will be the only address the service is bound to
            },
            _ => (KnownHostNames.Localhost, EndpointBindingMode.DualStackAnyAddresses), // Any other target host is treated as binding to all IPv4 AND IPv6 addresses
        };
    }
}
