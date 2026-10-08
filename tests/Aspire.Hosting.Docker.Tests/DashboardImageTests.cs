// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Docker.Tests;

public class DashboardImageTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    public async Task DefaultImage_StartsHealthyDashboard()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddLogging(logging => logging.AddXunit(output));

        var dashboard = builder.AddContainer("dashboard", DashboardImage.Name, DashboardImage.ResolveTag())
            .WithEnvironment("DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS", "true")
            .WithHttpEndpoint(targetPort: 18888)
            .WithHttpHealthCheck("/health");

        await using var app = builder.Build();
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await app.StartAsync(startupTimeout.Token);

        using var readinessTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(dashboard.Resource.Name, readinessTimeout.Token);

        using var client = app.CreateHttpClient(dashboard.Resource.Name, "http");
        using var response = await client.GetAsync("/health", readinessTimeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await app.StopAsync();
    }

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
