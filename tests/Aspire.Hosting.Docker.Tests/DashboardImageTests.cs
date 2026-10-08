// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Shared;

namespace Aspire.Hosting.Docker.Tests;

public class DashboardImageTests
{
    [Fact]
    public void WithDashboard_UsesDefaultImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environment = builder.AddDockerComposeEnvironment("docker").WithDashboard();
        var image = Assert.Single(environment.Resource.Dashboard!.Resource.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal("mcr.microsoft.com/aspire/nightly/dashboard", image.Image);
        Assert.Equal("13.6", image.Tag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithDashboard_PreservesImageOverrides(bool overrideRepository)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var environment = builder.AddDockerComposeEnvironment("docker").WithDashboard(dashboard =>
        {
            if (overrideRepository)
            {
                dashboard.WithImage("example.com/custom/dashboard", "custom");
            }
            else
            {
                dashboard.WithImageTag("custom");
            }
        });
        var image = Assert.Single(environment.Resource.Dashboard!.Resource.Annotations.OfType<ContainerImageAnnotation>());

        Assert.Equal(overrideRepository ? "example.com/custom/dashboard" : "mcr.microsoft.com/aspire/nightly/dashboard", image.Image);
        Assert.Equal("custom", image.Tag);
    }

    [Fact]
    public void ResolveTag_FromRunningAssembly_UsesBuildTimeImageTag()
    {
        Assert.Equal("13.6", DashboardImage.ResolveTag());
    }
}
