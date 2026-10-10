// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Network;
using Azure.Provisioning.PrivateDns;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring internal load balancers and private DNS.
/// </summary>
public static class AzureInternalLoadBalancerExtensions
{
    /// <summary>
    /// Configures the resource with an internal load balancer and private DNS.
    /// </summary>
    /// <typeparam name="T">The resource type that supports an internal load balancer.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="virtualNetwork">The virtual network containing the resource's delegated subnet.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// The resource must first be configured with <c>WithDelegatedSubnet</c>. A private DNS zone named
    /// after its default domain is linked to <paramref name="virtualNetwork"/>, and a wildcard record
    /// resolves hostnames to the internal load balancer's static IP. This method has no effect in run mode.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when an argument is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the delegated subnet is missing, belongs to a different network, or the resource already uses a different internal network.</exception>
    [AspireExport("withNetworkInternalLoadBalancer", MethodName = "withInternalLoadBalancer")]
    public static IResourceBuilder<T> WithInternalLoadBalancer<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<AzureVirtualNetworkResource> virtualNetwork)
        where T : IAzureInternalIngressResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(virtualNetwork);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        if (!builder.Resource.TryGetLastAnnotation<DelegatedSubnetAnnotation>(out var delegatedSubnet))
        {
            throw new InvalidOperationException(
                $"Azure resource '{builder.Resource.Name}' must use a delegated subnet before it can use an internal load balancer.");
        }

        var subnet = builder.ApplicationBuilder.Resources
            .OfType<AzureSubnetResource>()
            .SingleOrDefault(candidate =>
                ReferenceExpression.Create($"{candidate.Id}").ValueExpression == delegatedSubnet.SubnetId.ValueExpression);
        if (subnet is null || !ReferenceEquals(subnet.Parent, virtualNetwork.Resource))
        {
            throw new InvalidOperationException(
                $"The delegated subnet for Azure resource '{builder.Resource.Name}' must belong to virtual network '{virtualNetwork.Resource.Name}'.");
        }

        if (builder.Resource.TryGetLastAnnotation<InternalLoadBalancerAnnotation>(out var existing))
        {
            if (!ReferenceEquals(existing.VirtualNetwork, virtualNetwork.Resource))
            {
                throw new InvalidOperationException(
                    $"Azure resource '{builder.Resource.Name}' is already linked to virtual network '{existing.VirtualNetwork.Name}'.");
            }

            return builder;
        }

        builder.WithAnnotation(new InternalLoadBalancerAnnotation(virtualNetwork.Resource));
        var privateDnsResourceName = $"{builder.Resource.Name}-private-dns";
        if (privateDnsResourceName.Length > 64)
        {
            var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(privateDnsResourceName))).ToLowerInvariant()[..8];
            privateDnsResourceName = $"{privateDnsResourceName[..55]}-{hash}";
        }

        // Capture the resolved subnet resource (rather than re-deriving it from
        // DelegatedSubnetAnnotation, which only stores a ReferenceExpression ID) so the Bicep-generation
        // callback below can revalidate its delegation at deployment-model-finalization time.
        builder.ApplicationBuilder
            .AddAzureInfrastructure(
                privateDnsResourceName,
                infrastructure => AddPrivateDns(infrastructure, builder.Resource, virtualNetwork.Resource, subnet))
            .WithParentRelationship(builder.Resource)
            .WithRelationship(virtualNetwork.Resource, "Virtual network link");

        return builder.WithRelationship(virtualNetwork.Resource, "Internal network");
    }

    private static void AddPrivateDns(
        AzureResourceInfrastructure infrastructure,
        IAzureInternalIngressResource resource,
        AzureVirtualNetworkResource virtualNetwork,
        AzureSubnetResource subnet)
    {
        // WithServiceDelegation is explicitly last-write-wins, so the subnet delegation validated at
        // WithInternalLoadBalancer call time can be replaced afterward by an unrelated later call on the
        // same subnet (for example another WithDelegatedSubnet target, or a direct WithServiceDelegation
        // call). Revalidate the delegation here, during infrastructure generation, so call order cannot
        // emit this resource with IsInternal = true (and this private DNS module) while its subnet is no
        // longer delegated to the resource's required service.
        subnet.TryGetLastAnnotation<AzureSubnetServiceDelegationAnnotation>(out var delegation);
        if (delegation is null || !string.Equals(
                delegation.ServiceName,
                resource.DelegatedSubnetServiceName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The delegated subnet for Azure resource '{infrastructure.AspireResource.Name}' must remain delegated to " +
                $"'{resource.DelegatedSubnetServiceName}' for an internal load balancer, but it is " +
                $"{(delegation is null ? "no longer delegated" : $"delegated to '{delegation.ServiceName}'")}.");
        }

        var virtualNetworkResource = (VirtualNetwork)virtualNetwork.AddAsExistingResource(infrastructure);
        var dnsZone = new PrivateDnsZone(
            Infrastructure.NormalizeBicepIdentifier($"{infrastructure.AspireResource.Name}_privateDns"))
        {
            // The domain is a runtime output. A separate deployment passes it as a module
            // parameter, which Bicep permits as a resource name.
            Name = resource.DefaultDomain.AsProvisioningParameter(infrastructure),
            Location = new AzureLocation("global"),
        };
        infrastructure.Add(dnsZone);

        var wildcardRecord = new PrivateDnsARecord(
            Infrastructure.NormalizeBicepIdentifier($"{infrastructure.AspireResource.Name}_wildcard"))
        {
            Name = "*",
            Parent = dnsZone,
            TtlInSeconds = 3600,
            PrivateDnsARecords =
            {
                new PrivateDnsARecordInfo
                {
                    IPv4Address = resource.StaticIp.AsProvisioningParameter(infrastructure),
                },
            },
        };
        infrastructure.Add(wildcardRecord);

        var vnetLink = new VirtualNetworkLink(
            Infrastructure.NormalizeBicepIdentifier($"{infrastructure.AspireResource.Name}_vnetLink"))
        {
            Name = $"{infrastructure.AspireResource.Name}-link",
            Parent = dnsZone,
            Location = new AzureLocation("global"),
            RegistrationEnabled = false,
            VirtualNetworkId = virtualNetworkResource.Id,
        };
        infrastructure.Add(vnetLink);
    }
}
