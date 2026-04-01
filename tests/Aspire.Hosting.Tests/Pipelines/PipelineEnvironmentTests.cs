// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class PipelineEnvironmentTests
{
    [Fact]
    public async Task GetEnvironmentAsync_NoEnvironments_ReturnsLocalPipelineEnvironment()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var pipeline = new DistributedApplicationPipeline(model);

        var environment = await pipeline.GetEnvironmentAsync();

        Assert.IsType<LocalPipelineEnvironment>(environment);
    }

    [Fact]
    public async Task GetEnvironmentAsync_OneEnvironmentWithPassingCheck_ReturnsIt()
    {
        var resources = new ResourceCollection();
        var env = new TestPipelineEnvironment("test-env");
        env.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(true)));
        resources.Add(env);

        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        var result = await pipeline.GetEnvironmentAsync();

        Assert.Same(env, result);
    }

    [Fact]
    public async Task GetEnvironmentAsync_OneEnvironmentWithFailingCheck_ReturnsLocalPipelineEnvironment()
    {
        var resources = new ResourceCollection();
        var env = new TestPipelineEnvironment("test-env");
        env.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(false)));
        resources.Add(env);

        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        var result = await pipeline.GetEnvironmentAsync();

        Assert.IsType<LocalPipelineEnvironment>(result);
    }

    [Fact]
    public async Task GetEnvironmentAsync_TwoEnvironments_OnePasses_ReturnsPassingOne()
    {
        var resources = new ResourceCollection();

        var env1 = new TestPipelineEnvironment("env1");
        env1.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(false)));
        resources.Add(env1);

        var env2 = new TestPipelineEnvironment("env2");
        env2.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(true)));
        resources.Add(env2);

        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        var result = await pipeline.GetEnvironmentAsync();

        Assert.Same(env2, result);
    }

    [Fact]
    public async Task GetEnvironmentAsync_TwoEnvironments_BothPass_Throws()
    {
        var resources = new ResourceCollection();

        var env1 = new TestPipelineEnvironment("env1");
        env1.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(true)));
        resources.Add(env1);

        var env2 = new TestPipelineEnvironment("env2");
        env2.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(true)));
        resources.Add(env2);

        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pipeline.GetEnvironmentAsync());

        Assert.Contains("env1", ex.Message);
        Assert.Contains("env2", ex.Message);
        Assert.Contains("Multiple pipeline environments", ex.Message);
    }

    [Fact]
    public async Task GetEnvironmentAsync_EnvironmentWithoutCheckAnnotation_TreatedAsNonRelevant()
    {
        var resources = new ResourceCollection();
        var env = new TestPipelineEnvironment("no-check-env");
        // No PipelineEnvironmentCheckAnnotation added
        resources.Add(env);

        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        var result = await pipeline.GetEnvironmentAsync();

        Assert.IsType<LocalPipelineEnvironment>(result);
    }

    [Fact]
    public async Task GetEnvironmentAsync_ResourcesAddedAfterConstruction_AreDetected()
    {
        var resources = new ResourceCollection();
        var model = new DistributedApplicationModel(resources);
        var pipeline = new DistributedApplicationPipeline(model);

        // Add environment AFTER pipeline construction (simulates builder.AddResource after pipeline is created)
        var env = new TestPipelineEnvironment("late-env");
        env.Annotations.Add(new PipelineEnvironmentCheckAnnotation(_ => Task.FromResult(true)));
        resources.Add(env);

        var result = await pipeline.GetEnvironmentAsync();

        Assert.Same(env, result);
    }

    private sealed class TestPipelineEnvironment(string name) : Resource(name), IPipelineEnvironment
    {
    }
}
