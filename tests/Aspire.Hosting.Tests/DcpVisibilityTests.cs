// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class DcpVisibilityTests
{
    [Fact]
    public void EnsureNoTypesFromDcpNamespaceArePublic()
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var types = hostingAssembly.GetExportedTypes();
        var dcpNamespaceTypes = types.Where(t => t.FullName!.Contains("Dcp", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(dcpNamespaceTypes);
    }
}
