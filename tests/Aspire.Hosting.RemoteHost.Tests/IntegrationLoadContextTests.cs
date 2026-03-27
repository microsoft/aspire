// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class IntegrationLoadContextTests
{
    [Fact]
    public void AspireTypeSystem_IsSharedFromDefaultContext()
    {
        var alc = new IntegrationLoadContext([AppContext.BaseDirectory], NullLogger.Instance);

        var sharedAssembly = alc.LoadFromAssemblyName(new AssemblyName("Aspire.TypeSystem"));

        Assert.Same(typeof(AtsContext).Assembly, sharedAssembly);
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(sharedAssembly));
    }

    [Fact]
    public void VersionUnification_DefersToDefaultContext_WhenAlreadyLoaded()
    {
        // Aspire.Hosting is already in the default context (test project references it),
        // so version unification will defer to default rather than loading a second copy.
        var alc = new IntegrationLoadContext([AppContext.BaseDirectory], NullLogger.Instance);

        var assembly = alc.LoadFromAssemblyName(new AssemblyName("Aspire.Hosting"));
        Assert.NotNull(assembly);
        Assert.Same(typeof(IDistributedApplicationBuilder).Assembly, assembly);
    }
}
