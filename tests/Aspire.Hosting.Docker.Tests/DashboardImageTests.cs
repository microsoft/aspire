// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Docker.Tests;

public class DashboardImageTests
{
    [Fact]
    public void Name_UsesCanonicalAspireNightlyRepository()
    {
        Assert.Equal("mcr.microsoft.com/aspire/nightly/dashboard", DashboardImage.Name);
    }

    [Fact]
    public void ResolveTag_FromRunningAssembly_UsesProductVersion()
    {
        Assert.Equal("17.0", DashboardImage.ResolveTag());
    }
}
