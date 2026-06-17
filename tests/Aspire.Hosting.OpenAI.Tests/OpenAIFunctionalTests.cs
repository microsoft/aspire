// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.OpenAI.Tests;

public class OpenAIFunctionalTests
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/10977")]
    public async Task DependentResourceWaitsForOpenAIModelResourceWithHealthCheckToBeHealthy()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:resource-openai-apikey"] = "test-api-key";

        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddOpenAI("resource")
                      .AddModel("chat", "gpt-4o-mini")
                      .WithHealthCheck("blocking_check");

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }
    
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task DependentResourceWaitsForOpenAIResourceWithHealthCheckToBeHealthy()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);

        var healthCheckTcs = new TaskCompletionSource<HealthCheckResult>();

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Parameters:resource-openai-apikey"] = "test-api-key";

        builder.Services.AddHealthChecks().AddAsyncCheck("blocking_check", () =>
        {
            return healthCheckTcs.Task;
        });

        var resource = builder.AddOpenAI("resource")
                       .WithHealthCheck("blocking_check");

        Assert.Single(resource.Resource.Annotations, a => a is HealthCheckAnnotation hca && hca.Key == "resource_check");

        // Remove the default status page health check
        var statusPageHealthCheck = Enumerable.Single(resource.Resource.Annotations, x => x is HealthCheckAnnotation hca && hca.Key == "resource_check");
        resource.Resource.Annotations.Remove(statusPageHealthCheck);

        var dependentResource = builder.AddContainer("nginx", "mcr.microsoft.com/cbl-mariner/base/nginx", "1.22")
                                       .WaitFor(resource);

        using var app = builder.Build();

        var pendingStart = app.StartAsync(cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Waiting, cts.Token);

        healthCheckTcs.SetResult(HealthCheckResult.Healthy());

        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, cts.Token);

        await app.ResourceNotifications.WaitForResourceAsync(dependentResource.Resource.Name, KnownResourceStates.Running, cts.Token);

        await pendingStart;

        await app.StopAsync();
    }
}
