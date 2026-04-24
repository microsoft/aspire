// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class ComputeEnvironmentValidationTests
{
    [Fact]
    public async Task MultipleComputeEnvironments_WithUnboundComputeResource_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddResource(new TestComputeEnvironmentResource("env1"));
        builder.AddResource(new TestComputeEnvironmentResource("env2"));
        builder.AddResource(new TestComputeResource("api"));

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Contains("'api'", ex.Message);
        Assert.Contains("'env1'", ex.Message);
        Assert.Contains("'env2'", ex.Message);
        Assert.Contains("WithComputeEnvironment", ex.Message);
    }

    [Fact]
    public async Task MultipleComputeEnvironments_WithAllResourcesBound_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        builder.AddResource(new TestComputeEnvironmentResource("env2"));
        builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env1);

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();
    }

    [Fact]
    public async Task SingleComputeEnvironment_AutoBindsUnboundResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var api = builder.AddResource(new TestComputeResource("api"));

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        Assert.Same(env.Resource, api.Resource.GetComputeEnvironment());
    }

    private sealed class TestComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource
    {
    }

    private sealed class TestComputeResource(string name) : Resource(name), IComputeResource
    {
    }
}
