// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Yarp.Transforms;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ContainerTunnelTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task ContainerTunnelWorksWithYarp()
    {
        const string testName = "container-tunnel-works-with-yarp";
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration[KnownConfigNames.EnableContainerTunnel] = "true";

        var servicea = builder.AddProject<Projects.ServiceA>($"{testName}-servicea");

        var yarp = builder.AddYarp($"{testName}-yarp").WithConfiguration(conf =>
        {
            conf.AddRoute("/servicea/{**catch-all}", servicea).WithTransformPathRemovePrefix("/servicea");
        });

        using var app = builder.Build();

        // Use extra long timeout because if this is first time the tunnel is being used,
        // getting the base images and building the tunnel (client) proxy image may take a while.
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);
        await app.WaitForTextAsync("Application started.").DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        using var clientA = app.CreateHttpClient(yarp.Resource.Name, "http");
        var response = await clientA.GetAsync("/servicea/").DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.Equal("Hello World!", body);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task ProxylessEndpointWorksWithContainerTunnel()
    {
        var port = await Helpers.Network.GetAvailablePortAsync();

        const string testName = "proxyless-endpoint-container-tunnel";
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration[KnownConfigNames.EnableContainerTunnel] = "true";

        var servicea = builder.AddProject<Projects.ServiceA>($"{testName}-servicea")
            .WithEndpoint("http", e =>
            {
                e.Port = port;
                e.TargetPort = port;
                e.IsProxied = false;
            });

        var yarp = builder.AddYarp($"{testName}-yarp").WithConfiguration(conf =>
        {
            conf.AddRoute("/servicea/{**catch-all}", servicea).WithTransformPathRemovePrefix("/servicea");
        });

        await using var app = builder.Build();

        // Use extra long timeout because if this is first time the tunnel is being used,
        // getting the base images and building the tunnel (client) proxy image may take a while.
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);
        await app.WaitForTextAsync("Application started.").DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        using var clientA = app.CreateHttpClient(yarp.Resource.Name, "http");
        var response = await clientA.GetAsync("/servicea/").DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.True(response.IsSuccessStatusCode);
        var body = await response.Content.ReadAsStringAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.Equal("Hello World!", body);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task WaitingContainersCanUseTunnel()
    {
        const string testName = "waiting-containers-can-use-tunnel";
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration[KnownConfigNames.EnableContainerTunnel] = "true";

        var servicea = builder.AddProject<Projects.ServiceA>($"{testName}-servicea", launchProfileName: null)
            .WithHttpEndpoint();

        var container = builder.AddContainer($"{testName}-container", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
            .WithEnvironment("SERVICEA_URL", servicea.GetEndpoint("http"));

        var waitingContainer = builder.AddContainer($"{testName}-waiting", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
            .WaitFor(container);

        var waitingContainerConsumingEndpoint = builder.AddContainer($"{testName}-waiting-consuming", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
            .WithEnvironment("SERVICEA_URL", servicea.GetEndpoint("http"))
            .WaitFor(container);

        await using var app = builder.Build();

        // Use extra long timeout because if this is first time the tunnel is being used,
        // getting the base images and building the tunnel (client) proxy image may take a while.
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        foreach (var c in new[] { container, waitingContainer, waitingContainerConsumingEndpoint })
        {
            await app.ResourceNotifications.WaitForResourceAsync(c.Resource.Name, KnownResourceStates.Running)
            .DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.DockerPluginBuildx)]
    public async Task HostResourceCanWaitForTunnelDependentContainers()
    {
        const string testName = "host-resource-waits-for-tunnel-container";
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration[KnownConfigNames.EnableContainerTunnel] = "true";

        var upstreamService = builder.AddProject<Projects.ServiceA>($"{testName}-upstream", launchProfileName: null)
            .WithHttpEndpoint();

        var tunnelDependentContainer = builder.AddContainer($"{testName}-container", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
            .WithEnvironment("UPSTREAM_SERVICE_URL", upstreamService.GetEndpoint("http"))
            .WaitFor(upstreamService);

        var downstreamService = builder.AddProject<Projects.ServiceB>($"{testName}-downstream", launchProfileName: null)
            .WithHttpEndpoint()
            .WaitFor(tunnelDependentContainer);

        await using var app = builder.Build();

        // Use extra long timeout because if this is first time the tunnel is being used,
        // getting the base images and building the tunnel (client) proxy image may take a while.
        await app.StartAsync().DefaultTimeout(TestConstants.ExtraLongTimeoutDuration);

        foreach (var resourceName in new[] { upstreamService.Resource.Name, tunnelDependentContainer.Resource.Name, downstreamService.Resource.Name })
        {
            await app.ResourceNotifications.WaitForResourceAsync(resourceName, KnownResourceStates.Running)
                .DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

}
