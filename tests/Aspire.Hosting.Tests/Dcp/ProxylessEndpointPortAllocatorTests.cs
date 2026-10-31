// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dcp;

public class ProxylessEndpointPortAllocatorTests
{
    [Fact]
    public void AllocatePortUsesIncrementalCandidatesAfterSuccess()
    {
        var allocator = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10004,
            randomWalkOffset: 2,
            randomWalkStep: 3,
            tryProbe: static (_, _) => true);

        Assert.Equal(10002, allocator.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(10003, allocator.AllocatePort(CreateEndpoint("b")));
        Assert.Equal(10004, allocator.AllocatePort(CreateEndpoint("c")));
    }

    [Fact]
    public void AllocatePortJumpsToRandomWalkCandidateAfterFailure()
    {
        var allocator = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10004,
            randomWalkOffset: 0,
            randomWalkStep: 2,
            tryProbe: static (port, _) => port != 10000);

        Assert.Equal(10002, allocator.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(10003, allocator.AllocatePort(CreateEndpoint("b")));
    }

    [Fact]
    public void AllocatePortSkipsExcludedPortsAndExhaustsRangeWithoutRepeats()
    {
        var allocator = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10004,
            randomWalkOffset: 1,
            randomWalkStep: 2,
            tryProbe: static (_, _) => true);

        allocator.ExcludePort(10001);

        var allocatedPorts = new[]
        {
            allocator.AllocatePort(CreateEndpoint("a")),
            allocator.AllocatePort(CreateEndpoint("b")),
            allocator.AllocatePort(CreateEndpoint("c")),
            allocator.AllocatePort(CreateEndpoint("d"))
        };

        Assert.Equal(new[] { 10003, 10004, 10000, 10002 }, allocatedPorts);
        Assert.Throws<InvalidOperationException>(() =>
        {
            allocator.AllocatePort(CreateEndpoint("e"));
        });
    }

    [Fact]
    public void AllocatePortReturnsSameReservedPortForSameEndpoint()
    {
        var allocator = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10001,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: static (_, _) => true);
        var endpoint = CreateEndpoint("endpoint");

        Assert.Equal(10000, allocator.AllocatePort(endpoint));
        Assert.Equal(10000, allocator.AllocatePort(endpoint));
        Assert.Equal(10001, allocator.AllocatePort(CreateEndpoint("other")));
    }

    [Fact]
    public void AllocatePortWithoutEndpointReservesUniquePort()
    {
        var allocator = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10001,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: static (_, _) => true);

        Assert.Equal(10000, allocator.AllocatePort(ProtocolType.Tcp));
        Assert.Equal(10001, allocator.AllocatePort(ProtocolType.Tcp));
    }

    [Fact]
    public void AllocatorsForDifferentApplicationsInTheSameProcessDoNotShareAPort()
    {
        // Each application gets its own allocator, and the probe releases the port before the workload binds it.
        // Parallel AppHosts in one process (for example, test classes) must still never receive the same port,
        // otherwise one application's proxy forwards to another application's workload.
        var port = GetUnusedPortOutsideDefaultRange();
        var options = Options.Create(new DcpOptions
        {
            ProxylessEndpointPortRangeStart = port,
            ProxylessEndpointPortRangeEnd = port
        });

        using (var firstApplicationAllocator = new ProxylessEndpointPortAllocator(options))
        {
            Assert.Equal(port, firstApplicationAllocator.AllocatePort(CreateEndpoint("dashboard")));

            using var secondApplicationAllocator = new ProxylessEndpointPortAllocator(options);
            Assert.Throws<InvalidOperationException>(() => secondApplicationAllocator.AllocatePort(CreateEndpoint("webapp")));
        }

        // Disposing the owning application's allocator releases its reservations.
        using var laterApplicationAllocator = new ProxylessEndpointPortAllocator(options);
        Assert.Equal(port, laterApplicationAllocator.AllocatePort(CreateEndpoint("webapp")));
    }

    [Fact]
    public void AllocatorsSharingReservationsSkipPortsReservedByEachOther()
    {
        var reservations = new ProxylessEndpointPortReservations();
        var first = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10003,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: static (_, _) => true,
            reservations);
        using var second = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10003,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: static (_, _) => true,
            reservations);

        Assert.Equal(10000, first.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(10001, first.AllocatePort(ProtocolType.Tcp));
        Assert.Equal(10002, second.AllocatePort(CreateEndpoint("a")));

        first.Dispose();

        using var third = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10003,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: static (_, _) => true,
            reservations);
        Assert.Equal(10000, third.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(10001, third.AllocatePort(CreateEndpoint("b")));
        Assert.Equal(10003, third.AllocatePort(CreateEndpoint("c")));
    }

    private static int GetUnusedPortOutsideDefaultRange()
    {
        // Use ports above the default proxyless range, which other tests in this process allocate from through the
        // shared reservations. Select with the allocator's own IPv4+IPv6 probe so discovery and allocation agree.
        var rangeEnd = new DcpOptions().ProxylessEndpointPortRangeEnd;
        for (var port = rangeEnd + 1 + Random.Shared.Next(1000); port <= IPEndPoint.MaxPort; port++)
        {
            if (ProxylessEndpointPortAllocator.TryProbePort(port, ProtocolType.Tcp))
            {
                return port;
            }
        }

        Assert.Fail($"No available port was found above {rangeEnd}.");
        return 0;
    }

    private static EndpointAnnotation CreateEndpoint(string name)
    {
        return new EndpointAnnotation(ProtocolType.Tcp, name: name, isProxied: false);
    }
}
