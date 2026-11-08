// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Core.Tests;

public class RuntimeObservationTests
{
    [Fact]
    public async Task DependencyProjectionPreservesOrderedRedactedObservationsAndPriorSnapshots()
    {
        using var model = new ApplicationModel();
        var consumer = model.AddResource("consumer", "example/consumer");
        var second = model.AddResource("z-producer", "example/producer");
        var first = model.AddResource("a-producer", "example/producer");
        model.AddSecretParameter("password", "private-value");
        model.AddDependency(consumer, second);
        model.AddDependency(consumer, first);
        for (var index = 0; index < 100; index++)
        {
            model.AddResource($"unrelated-{index}", "example/unrelated");
        }
        using var runtime = new RuntimeGeneration(model.Seal());
        using var consumerOwner = runtime.ClaimResource(consumer);
        using var firstOwner = runtime.ClaimResource(first);
        using var secondOwner = runtime.ClaimResource(second);
        firstOwner.Publish(ResourceState.Running, healthy: true, ["http://localhost:8000"]);
        secondOwner.Publish(ResourceState.Starting, healthy: false, []);
        firstOwner.DefineCommand("z-refresh", "Refresh private-value");
        firstOwner.DefineCommand("a-stop", "Stop");
        var initial = consumerOwner.ReadDependencies();
        firstOwner.Publish(ResourceState.Stopped, healthy: false, []);
        var updated = consumerOwner.ReadDependencies();
        var all = runtime.Read();
        Assert.Equal(all.GenerationId, updated.GenerationId);
        Assert.Equal(all.Version, updated.Version);
        Assert.Equal(103, all.Resources.Length);
        Assert.Equal(["a-producer", "z-producer"], updated.Resources.Select(resource => resource.Name));

        await Verify(new { Initial = initial, Updated = updated }).UseDirectory("Snapshots");
    }

    [Fact]
    public void EmptyDependencyProjectionRetainsTheCurrentGenerationAndVersion()
    {
        using var model = new ApplicationModel();
        var resource = model.AddResource("standalone", "example/resource");
        using var runtime = new RuntimeGeneration(model.Seal());
        using var owner = runtime.ClaimResource(resource);
        owner.Publish(ResourceState.Running, healthy: true, []);
        var dependencies = owner.ReadDependencies();
        var all = runtime.Read();

        Assert.Equal(all.GenerationId, dependencies.GenerationId);
        Assert.Equal(all.Version, dependencies.Version);
        Assert.False(dependencies.Resources.IsDefault);
        Assert.Empty(dependencies.Resources);
        Assert.Empty(Assert.Single(all.Resources).Commands);
    }
}
