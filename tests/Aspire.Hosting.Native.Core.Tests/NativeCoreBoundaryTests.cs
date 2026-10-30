// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeCoreBoundaryTests
{
    [Fact]
    public void KernelHasOnlyBclRuntimeDependencies()
    {
        var assembly = typeof(ApplicationModel).Assembly;

        Assert.Equal("Aspire.Hosting.Native.Core", assembly.GetName().Name);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.StartsWith("System.", reference.Name!, StringComparison.Ordinal));
    }
}
