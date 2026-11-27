// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpEndpointAllocatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProxylessPublicPortAllocationPreservesContainerTargetPorts(bool container)
    {
        IResource resource = container
            ? new ContainerResource("app")
            : new ExecutableResource("app", "app", ".");
        if (container)
        {
            resource.Annotations.Add(new ContainerImageAnnotation { Image = "image" });
        }

        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http", targetPort: container ? 80 : null, isProxied: false);
        resource.Annotations.Add(endpoint);
        var model = new DistributedApplicationModel([resource]);
        using var ports = CreatePortAllocator();
        var allocator = CreateAllocator(model, ports);
        allocator.PrepareEndpoints(model);

        var publicPort = allocator.AllocatePublicPort(resource, endpoint);

        Assert.Equal(20000, publicPort);
        Assert.Equal(publicPort, endpoint.Port);
        Assert.Equal(container ? 80 : publicPort, endpoint.TargetPort);
        Assert.Equal(publicPort, allocator.AllocatePublicPort(resource, endpoint));
    }

    [Fact]
    public void ExecutableReplicasAllocateDistinctTargetPortsWithoutMutatingSharedEndpoint()
    {
        var resource = new ExecutableResource("app", "app", ".");
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http", port: 20000);
        resource.Annotations.Add(endpoint);
        var model = new DistributedApplicationModel([resource]);
        using var ports = CreatePortAllocator();
        var allocator = CreateAllocator(model, ports);
        allocator.PrepareEndpoints(model);

        Assert.Equal(20001, allocator.AllocateExecutableTargetPort(endpoint));
        Assert.Equal(20002, allocator.AllocateExecutableTargetPort(endpoint));
        Assert.Null(endpoint.TargetPort);
        Assert.Equal(20000, allocator.AllocatePublicPort(resource, endpoint));
    }

    [Theory]
    [InlineData(true, false, 20000)]
    [InlineData(false, false, 20000)]
    [InlineData(false, true, 20000)]
    [InlineData(true, true, null)]
    public void PublicPortRandomizationPreservesExplicitProxylessPorts(bool proxied, bool randomize, int? expectedPort)
    {
        var resource = new ExecutableResource("app", "app", ".");
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http", port: 20000, isProxied: proxied);
        resource.Annotations.Add(endpoint);
        var model = new DistributedApplicationModel([resource]);
        using var ports = CreatePortAllocator();
        var allocator = CreateAllocator(model, ports, randomize);
        allocator.PrepareEndpoints(model);

        Assert.Equal(expectedPort, allocator.AllocatePublicPort(resource, endpoint));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void PreparationResolvesDefaultProxyBehaviorBeforeAllocation(bool randomize, bool persistent, bool expectedProxy)
    {
        var resource = new ContainerResource("app");
        resource.Annotations.Add(new ContainerImageAnnotation { Image = "image" });
        resource.Annotations.Add(new ContainerLifetimeAnnotation
        {
            Lifetime = persistent ? ContainerLifetime.Persistent : ContainerLifetime.Session
        });
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http", targetPort: 80);
        resource.Annotations.Add(endpoint);
        var model = new DistributedApplicationModel([resource]);
        using var ports = CreatePortAllocator();
        var allocator = CreateAllocator(model, ports, randomize);

        var producer = Assert.Single(allocator.PrepareEndpoints(model));

        Assert.Same(resource, producer.ModelResource);
        Assert.Same(endpoint, Assert.Single(producer.Endpoints));
        Assert.Equal(expectedProxy, endpoint.IsProxied);
        Assert.Null(endpoint.AllocatedEndpoint);
    }

    [Theory]
    [InlineData("0.0.0.0", EndpointBindingMode.IPv4AnyAddresses)]
    [InlineData("::", EndpointBindingMode.IPv6AnyAddresses)]
    [InlineData("localhost", EndpointBindingMode.SingleAddress)]
    public void LocalhostAllocationNormalizesBindingAndPreservesBackendTargetExpression(string targetHost, EndpointBindingMode bindingMode)
    {
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http") { TargetHost = targetHost };

        Assert.True(DcpEndpointAllocator.TryAllocateLocalhostEndpoint(
            endpoint, "service", "127.0.0.1", 20000, hasCompleteAddress: true,
            targetPortExpression: "backend-target-port", allowPending: false));

        var allocated = Assert.IsType<AllocatedEndpoint>(endpoint.AllocatedEndpoint);
        Assert.Equal("localhost", allocated.Address);
        Assert.Equal(20000, allocated.Port);
        Assert.Equal(bindingMode, allocated.BindingMode);
        Assert.Equal("backend-target-port", allocated.TargetPortExpression);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PendingAddressDoesNotAllocateEndpointAndStrictAllocationFails(bool proxied)
    {
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http", isProxied: proxied);

        Assert.False(DcpEndpointAllocator.TryAllocateLocalhostEndpoint(
            endpoint, "service", null, null, hasCompleteAddress: false, "target", allowPending: true));
        Assert.Null(endpoint.AllocatedEndpoint);
        if (proxied)
        {
            Assert.Throws<InvalidDataException>(() => DcpEndpointAllocator.TryAllocateLocalhostEndpoint(
                endpoint, "service", null, null, hasCompleteAddress: false, "target", allowPending: false));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => DcpEndpointAllocator.TryAllocateLocalhostEndpoint(
                endpoint, "service", null, null, hasCompleteAddress: false, "target", allowPending: false));
        }
    }

    private static ProxylessEndpointPortAllocator CreatePortAllocator()
        => new(20000, 20010, randomWalkOffset: 0, randomWalkStep: 1, static (_, _) => true);

    private static DcpEndpointAllocator CreateAllocator(
        DistributedApplicationModel model,
        ProxylessEndpointPortAllocator ports,
        bool randomize = false)
    {
        var configuration = new ConfigurationManager();
        var options = Options.Create(new DcpOptions { RandomizePorts = randomize });
        var state = new AppHostLocalStateStore(configuration, NullLogger<AppHostLocalStateStore>.Instance);
        var store = new PersistentPortAllocationStore(state, NullLogger<PersistentPortAllocationStore>.Instance);
        var persistentPorts = new PersistentPortAllocator(model, configuration, options, ports, store, NullLogger<PersistentPortAllocator>.Instance);
        return new DcpEndpointAllocator(options, persistentPorts, NullLogger<DcpEndpointAllocator>.Instance);
    }
}
