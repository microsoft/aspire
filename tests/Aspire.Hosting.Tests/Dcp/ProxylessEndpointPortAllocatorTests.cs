// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Dcp;

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

    private static EndpointAnnotation CreateEndpoint(string name)
    {
        return new EndpointAnnotation(ProtocolType.Tcp, name: name, isProxied: false);
    }
}
