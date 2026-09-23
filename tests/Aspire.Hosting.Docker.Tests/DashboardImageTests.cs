// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Docker.Tests;

public class DashboardImageTests
{
    [Fact]
    public void ResolveTag_FromRunningAssembly_UsesBuildTimeImageTag()
    {
        Assert.Equal("13.6", DashboardImage.ResolveTag());
    }
}
