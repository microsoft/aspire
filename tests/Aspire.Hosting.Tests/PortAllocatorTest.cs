// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class PortAllocatorTest
{
    [Fact]
    public void CanAllocatePorts()
    {
        var allocator = new PortAllocator(1000);
        var port1 = allocator.AllocatePort();
        allocator.AddUsedPort(port1);
        var port2 = allocator.AllocatePort();

        Assert.Equal(1000, port1);
        Assert.Equal(1001, port2);
    }

    [Fact]
    public void SkipUsedPorts()
    {
        var allocator = new PortAllocator(1000);
        allocator.AddUsedPort(1000);
        allocator.AddUsedPort(1001);
        allocator.AddUsedPort(1003);
        var port1 = allocator.AllocatePort();
        allocator.AddUsedPort(port1);
        var port2 = allocator.AllocatePort();

        Assert.Equal(1002, port1);
        Assert.Equal(1004, port2);
    }
}
