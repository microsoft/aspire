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
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15358")]
    public async Task ProxylessEndpointWorksWithContainerTunnel()
    {
        var port = await Helpers.Network.GetAvailablePortAsync();

        const string testName = "proxyless-endpoint-works-with-container-tunnel";
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

}
