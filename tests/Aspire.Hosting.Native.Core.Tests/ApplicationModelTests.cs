// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Model;

namespace Aspire.Hosting.Native.Core.Tests;

public class ApplicationModelTests
{
    [Fact]
    public void ResourceIdentityBelongsToItsGeneration()
    {
        using var model = new ApplicationModel();
        var resource = model.AddResource("cache", "example.redis/Redis");
        var snapshot = model.ReadResource(resource);

        Assert.Equal(model.GenerationId, resource.GenerationId);
        Assert.NotEqual(Guid.Empty, resource.ResourceId);
        Assert.Equal(resource, snapshot.Handle);
        Assert.Equal("cache", snapshot.Name);
        Assert.Equal("example.redis/Redis", snapshot.TypeId);
        Assert.Empty(snapshot.Dependencies);
    }

    [Fact]
    public void DuplicateNamesAreRejectedWithoutChangingDeclarations()
    {
        using var model = new ApplicationModel();
        var cache = model.AddResource("cache", "example.redis/Redis");

        Assert.Throws<InvalidOperationException>(() => model.AddResource("CACHE", "example.redis/Redis"));
        Assert.Equal(cache, Assert.Single(model.Inspect().Resources).Handle);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1cache")]
    [InlineData("cache_name")]
    [InlineData("../cache")]
    [InlineData("cache\n")]
    public void InvalidNamesDoNotAllocateIdentities(string name)
    {
        using var model = new ApplicationModel();

        Assert.Throws<ArgumentException>(() => model.AddResource(name, "example.redis/Redis"));
        Assert.Empty(model.Inspect().Resources);
    }

    [Fact]
    public void ResourceNameLimitIsExact()
    {
        using var model = new ApplicationModel();
        var name = new string('a', 128);

        var resource = model.AddResource(name, "example.redis/Redis");
        Assert.Equal(name, model.ReadResource(resource).Name);
        Assert.Throws<ArgumentException>(() => model.AddResource(name + "a", "example.redis/Redis"));
        Assert.Single(model.Inspect().Resources);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Redis")]
    [InlineData("/Redis")]
    [InlineData("example.redis/")]
    [InlineData("example.redis/Redis/Other")]
    [InlineData("example.redis/Redis ")]
    [InlineData("example.redis/Redis\0")]
    public void InvalidTypeIdentitiesDoNotReserveNames(string typeId)
    {
        using var model = new ApplicationModel();

        Assert.Throws<ArgumentException>(() => model.AddResource("cache", typeId));
        model.AddResource("cache", "example.redis/Redis");
        Assert.Single(model.Inspect().Resources);
    }

    [Fact]
    public void ForgedAndForeignReferencesAreRejected()
    {
        using var model = new ApplicationModel();
        using var other = new ApplicationModel();
        var cache = model.AddResource("cache", "example.redis/Redis");
        var foreign = other.AddResource("cache", "example.redis/Redis");

        Assert.Throws<ArgumentException>(() => model.ReadResource(foreign));
        Assert.Throws<ArgumentException>(() => model.ReadResource(new ResourceHandle(model.GenerationId, Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => model.AddDependency(cache, foreign));
        Assert.Throws<ArgumentException>(() => model.AddDependency(foreign, cache));
        Assert.Empty(model.ReadResource(cache).Dependencies);
    }

    [Fact]
    public void DependencyCyclesAreRejectedAtomically()
    {
        using var model = new ApplicationModel();
        var cache = model.AddResource("cache", "example.redis/Redis");
        var web = model.AddResource("web", "example.javascript/Application");
        var proxy = model.AddResource("proxy", "example.proxy/Proxy");
        model.AddDependency(web, cache);
        model.AddDependency(proxy, web);

        Assert.Throws<InvalidOperationException>(() => model.AddDependency(cache, proxy));
        Assert.Throws<InvalidOperationException>(() => model.AddDependency(cache, cache));
        Assert.Empty(model.ReadResource(cache).Dependencies);
        Assert.Equal(cache, Assert.Single(model.ReadResource(web).Dependencies));
        Assert.Equal(web, Assert.Single(model.ReadResource(proxy).Dependencies));
    }

    [Fact]
    public void SharedDependenciesAndRepeatedDeclarationsArePermitted()
    {
        using var model = new ApplicationModel();
        var cache = model.AddResource("cache", "example.redis/Redis");
        var web = model.AddResource("web", "example.javascript/Application");
        var worker = model.AddResource("worker", "example.javascript/Application");
        model.AddDependency(web, cache);
        model.AddDependency(worker, cache);
        model.AddDependency(web, cache);

        Assert.Equal(cache, Assert.Single(model.ReadResource(web).Dependencies));
        Assert.Equal(cache, Assert.Single(model.ReadResource(worker).Dependencies));
    }

    [Fact]
    public void InspectionDoesNotSealOrShareMutableStorage()
    {
        using var model = new ApplicationModel();
        var web = model.AddResource("web", "example.javascript/Application");
        var before = model.Inspect();
        var cache = model.AddResource("cache", "example.redis/Redis");
        model.AddDependency(web, cache);
        var after = model.Inspect();

        Assert.Equal(web, Assert.Single(before.Resources).Handle);
        Assert.Empty(before.Resources[0].Dependencies);
        Assert.Equal(["cache", "web"], after.Resources.Select(resource => resource.Name));
        Assert.Equal(cache, Assert.Single(after.Resources[1].Dependencies));
    }

    [Fact]
    public void SealingIsIdempotentAndRejectsAllComposition()
    {
        using var model = new ApplicationModel();
        var web = model.AddResource("web", "example.javascript/Application");
        var cache = model.AddResource("cache", "example.redis/Redis");
        model.AddDependency(web, cache);
        var snapshot = model.Seal();

        Assert.Same(snapshot, model.Seal());
        Assert.Same(snapshot, model.Inspect());
        Assert.Throws<InvalidOperationException>(() => model.AddResource("worker", "example.javascript/Application"));
        Assert.Throws<InvalidOperationException>(() => model.AddDependency(web, cache));
        Assert.Equal(cache, Assert.Single(model.ReadResource(web).Dependencies));
    }

    [Fact]
    public void SnapshotsUseOrdinalNameOrdering()
    {
        using var model = new ApplicationModel();
        var web = model.AddResource("web", "example.javascript/Application");
        var zulu = model.AddResource("zulu", "example.redis/Redis");
        var alpha = model.AddResource("alpha", "example.redis/Redis");
        model.AddDependency(web, zulu);
        model.AddDependency(web, alpha);

        var snapshot = model.Seal();
        Assert.Equal(["alpha", "web", "zulu"], snapshot.Resources.Select(resource => resource.Name));
        Assert.Equal([alpha, zulu], snapshot.Resources[1].Dependencies);
    }

    [Fact]
    public async Task ConcurrentDeclarationsPreserveEveryResource()
    {
        using var model = new ApplicationModel();
        var resources = await Task.WhenAll(Enumerable.Range(0, 128)
            .Select(index => Task.Run(() => model.AddResource($"resource-{index}", "example/Resource"))));

        Assert.Equal(128, resources.Select(resource => resource.ResourceId).Distinct().Count());
        Assert.Equal(resources.OrderBy(resource => resource.ResourceId),
            model.Seal().Resources.Select(resource => resource.Handle).OrderBy(resource => resource.ResourceId));
    }

    [Fact]
    public async Task ConcurrentDuplicateDeclarationsHaveOneOwner()
    {
        using var model = new ApplicationModel();
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
            Record.Exception(() => model.AddResource("cache", "example.redis/Redis")))));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.All(outcomes.Where(outcome => outcome is not null), outcome => Assert.IsType<InvalidOperationException>(outcome));
        Assert.Single(model.Seal().Resources);
    }

    [Fact]
    public void DisposalRevokesAccessWithoutChangingSnapshots()
    {
        var model = new ApplicationModel();
        var cache = model.AddResource("cache", "example.redis/Redis");
        var snapshot = model.Seal();

        model.Dispose();
        model.Dispose();

        Assert.Throws<ObjectDisposedException>(() => model.ReadResource(cache));
        Assert.Throws<ObjectDisposedException>(model.Inspect);
        Assert.Throws<ObjectDisposedException>(model.Seal);
        Assert.Throws<ObjectDisposedException>(() => model.AddResource("web", "example.javascript/Application"));
        Assert.Throws<ObjectDisposedException>(() => model.AddDependency(cache, cache));
        Assert.Equal(cache, Assert.Single(snapshot.Resources).Handle);
    }
}
