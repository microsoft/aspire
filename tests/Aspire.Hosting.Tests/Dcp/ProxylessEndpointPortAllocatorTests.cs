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

    [Fact]
    public void AllocatorsSharingReservationsDoNotReprobePortsFoundInUse()
    {
        var reservations = new ProxylessEndpointPortReservations();
        var probedPorts = new List<int>();
        bool Probe(int port, ProtocolType protocol)
        {
            probedPorts.Add(port);
            return port != 10000;
        }

        using var first = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10002,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: Probe,
            reservations);
        Assert.Equal(10001, first.AllocatePort(CreateEndpoint("a")));
        first.Dispose();

        // A port another allocator found in use stays unavailable, even after that allocator is disposed.
        using var second = new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10002,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: Probe,
            reservations);
        Assert.Equal(10001, second.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(new[] { 10000, 10001, 10001 }, probedPorts);
    }

    [Fact]
    public void ExcludedPortsAreSharedAndOverlappingExclusionsSurviveOneAllocatorsDisposal()
    {
        var reservations = new ProxylessEndpointPortReservations();
        var first = CreateAllocator(reservations);
        var second = CreateAllocator(reservations);
        using var third = CreateAllocator(reservations);

        first.ExcludePort(10000);
        second.ExcludePort(10000);

        // An exclusion recorded by one allocator applies to every allocator sharing the reservations.
        Assert.Equal(10001, third.AllocatePort(CreateEndpoint("a")));
        Assert.Equal(2, reservations.GetReferenceCount(10000));

        // Disposing one excluding allocator keeps the exclusion the other allocator still requires.
        first.Dispose();
        Assert.Equal(1, reservations.GetReferenceCount(10000));
        Assert.Throws<InvalidOperationException>(() => third.AllocatePort(CreateEndpoint("b")));

        second.Dispose();
        Assert.Equal(0, reservations.GetReferenceCount(10000));
        Assert.Equal(10000, third.AllocatePort(CreateEndpoint("b")));
    }

    [Fact]
    public void ExcludePortDeduplicatesRepeatedExclusionsAndPortsAlreadyOwned()
    {
        var reservations = new ProxylessEndpointPortReservations();
        var allocator = CreateAllocator(reservations);

        allocator.ExcludePort(10000);
        allocator.ExcludePort(10000);
        Assert.Equal(1, reservations.GetReferenceCount(10000));

        Assert.Equal(10001, allocator.AllocatePort(CreateEndpoint("a")));
        allocator.ExcludePort(10001);
        Assert.Equal(1, reservations.GetReferenceCount(10001));

        allocator.Dispose();
        Assert.Equal(0, reservations.GetReferenceCount(10000));
        Assert.Equal(0, reservations.GetReferenceCount(10001));
        Assert.True(reservations.IsAvailable(10000));
        Assert.True(reservations.IsAvailable(10001));
    }

    [Fact]
    public void ExcludingAPermanentlyExcludedPortKeepsItPermanent()
    {
        var reservations = new ProxylessEndpointPortReservations();
        var prober = CreateAllocator(reservations, tryProbe: static (port, _) => port != 10000);
        Assert.Equal(10001, prober.AllocatePort(CreateEndpoint("a")));
        Assert.True(reservations.IsPermanentlyExcluded(10000));

        var excluder = CreateAllocator(reservations);
        excluder.ExcludePort(10000);
        excluder.Dispose();
        prober.Dispose();

        Assert.True(reservations.IsPermanentlyExcluded(10000));
        Assert.False(reservations.IsAvailable(10000));
    }

    [Fact]
    public void ProbeExceptionReleasesTheClaimAndPropagates()
    {
        var reservations = new ProxylessEndpointPortReservations();
        using var allocator = CreateAllocator(reservations, tryProbe: static (_, _) => throw new InvalidTimeZoneException());

        Assert.Throws<InvalidTimeZoneException>(() => allocator.AllocatePort(CreateEndpoint("a")));
        Assert.True(reservations.IsAvailable(10000));
        Assert.False(reservations.IsPermanentlyExcluded(10000));
    }

    [Fact]
    public void ReservationReferencesThrowInsteadOfOverflowing()
    {
        var reservations = new ProxylessEndpointPortReservations();
        for (var i = 0; i < 254; i++)
        {
            Assert.True(reservations.TryAddReference(10000));
        }

        Assert.Throws<InvalidOperationException>(() => reservations.TryAddReference(10000));
        Assert.Equal(254, reservations.GetReferenceCount(10000));
        Assert.False(reservations.TryReserve(10000));
    }

    private static ProxylessEndpointPortAllocator CreateAllocator(ProxylessEndpointPortReservations reservations, Func<int, ProtocolType, bool>? tryProbe = null)
    {
        return new ProxylessEndpointPortAllocator(
            rangeStart: 10000,
            rangeEnd: 10001,
            randomWalkOffset: 0,
            randomWalkStep: 1,
            tryProbe: tryProbe ?? (static (_, _) => true),
            reservations);
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
