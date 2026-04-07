// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class HealthCheckTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpHealthCheckThrowsIfReferencingEndpointByNameThatIsNotHttpScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpHealthCheck(endpointName: "nonhttp");
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpHealthCheckThrowsIfReferencingEndpointThatIsNotHttpScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp");

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
            container.WithHttpHealthCheck(() => container.GetEndpoint("nonhttp"));
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public void WithHttpsHealthCheckThrowsIfReferencingEndpointThatIsNotHttpsScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var ex = Assert.Throws<DistributedApplicationException>(() =>
        {
#pragma warning disable CS0618 // Type or member is obsolete
            builder.AddContainer("resource", "dummycontainer")
                .WithEndpoint(targetPort: 9999, scheme: "tcp", name: "nonhttp")
                .WithHttpsHealthCheck(endpointName: "nonhttp");
#pragma warning restore CS0618 // Type or member is obsolete
        });

        Assert.Equal(
            "Could not create HTTP health check for resource 'resource' as the endpoint with name 'nonhttp' and scheme 'tcp' is not an HTTP endpoint.",
            ex.Message
            );
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task VerifyWithHttpHealthCheckBlocksDependentResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(testOutputHelper);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();
        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddContainer("resource", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                              .WithHttpEndpoint(targetPort: 80)
                              .WithHttpHealthCheck(statusCode: 404)
                              .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddContainer("dependentresource", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        await rns.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await rns.WaitForResourceHealthyAsync(resource.Resource.Name).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await rns.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await pendingStart.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    public async Task BuildThrowsOnMissingHealthCheckRegistration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.Services.AddLogging(b => {
            b.AddFakeLogging();
        });

        builder.AddResource(new CustomResource("test"))
               .WithHealthCheck("test_check");
        var app = builder.Build();

        var ex = await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await app.StartAsync();
        }).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        Assert.Equal("A health check registration is missing. Check logs for more details.", ex.Message);

        var collector = app.Services.GetFakeLogCollector();
        var logs = collector.GetSnapshot();

        Assert.Contains(
            logs,
            l => l.Message == "The health check 'test_check' is not registered and is required for resource 'test'."
            );
    }

    private sealed class CustomChildResource(string name, CustomResource parent) : Resource(name), IResourceWithParent<CustomResource>
    {
        public CustomResource Parent => parent;
    }

    private sealed class CustomResource(string name) : Resource(name)
    {
    }
}
