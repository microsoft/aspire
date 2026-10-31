// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithBindMountTests
{
    [Fact]
    public void IdenticalBindMountIsNoOp()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = Path.GetFullPath("bind-source");

        var container = builder.AddContainer("container", "image")
            .WithBindMount(source, "/mnt/data", isReadOnly: true)
            .WithBindMount(source, "/mnt/data", isReadOnly: true);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(source, mount.Source);
        Assert.Equal("/mnt/data", mount.Target);
        Assert.True(mount.IsReadOnly);
    }

    [Fact]
    public void RelativeAndAbsoluteSourceForSamePathAreIdentical()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var absoluteSource = Path.GetFullPath("bind-source", builder.AppHostDirectory);

        var container = builder.AddContainer("container", "image")
            .WithBindMount("bind-source", "/mnt/data")
            .WithBindMount(absoluteSource, "/mnt/data");

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(absoluteSource, mount.Source);
    }

    [Fact]
    public void EquivalentAbsoluteSourcesAreIdentical()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = Path.GetFullPath("bind-source", builder.AppHostDirectory);

        var container = builder.AddContainer("container", "image")
            .WithBindMount("bind-source", "/mnt/data")
            .WithBindMount(Path.Combine(source, "."), "/mnt/data")
            .WithBindMount(source + Path.DirectorySeparatorChar, "/mnt/data");

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(source, mount.Source);
    }

    [Fact]
    public void DifferentTargetsAreBothAdded()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = Path.GetFullPath("bind-source");

        var container = builder.AddContainer("container", "image")
            .WithBindMount(source, "/mnt/one")
            .WithBindMount(source, "/mnt/two");

        Assert.Equal(["/mnt/one", "/mnt/two"], container.Resource.Annotations.OfType<ContainerMountAnnotation>().Select(m => m.Target));
    }

    [Fact]
    public void ConflictingSourceForSameTargetThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image")
            .WithBindMount(Path.GetFullPath("one"), "/mnt/data");

        var ex = Assert.Throws<InvalidOperationException>(() => container.WithBindMount(Path.GetFullPath("two"), "/mnt/data"));

        Assert.Contains("'/mnt/data'", ex.Message);
        Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public void ConflictingReadOnlyForSameTargetThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = Path.GetFullPath("bind-source");
        var container = builder.AddContainer("container", "image")
            .WithBindMount(source, "/mnt/data", isReadOnly: true);

        Assert.Throws<InvalidOperationException>(() => container.WithBindMount(source, "/mnt/data", isReadOnly: false));
        Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public void BindMountOverExistingVolumeTargetThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image")
            .WithVolume("data", "/mnt/data");

        var ex = Assert.Throws<InvalidOperationException>(() => container.WithBindMount(Path.GetFullPath("bind-source"), "/mnt/data"));

        Assert.Contains("volume", ex.Message);
        Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
    }

    [Fact]
    public async Task BindMountReappliedOnEveryStartIsAddedOnce()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = Path.GetFullPath("bind-source");
        var container = builder.AddContainer("container", "image");

        // Mirrors configuring a mount from a start event, which runs again whenever the resource restarts.
        builder.Eventing.Subscribe<BeforeResourceStartedEvent>(container.Resource, (_, _) =>
        {
            container.WithBindMount(source, "/mnt/data", isReadOnly: true);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new BeforeResourceStartedEvent(container.Resource, app.Services));
        await eventing.PublishAsync(new BeforeResourceStartedEvent(container.Resource, app.Services));

        Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
    }
}
