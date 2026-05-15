// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceBuilderLifetimeTests
{
    [Fact]
    public void WithPersistentLifetimeAddsContainerLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithPersistentLifetime();

        var annotation = container.Resource.Annotations.OfType<ContainerLifetimeAnnotation>().Single();
        Assert.Equal(ContainerLifetime.Persistent, annotation.Lifetime);
    }

    [Fact]
    public void WithPersistentLifetimeRejectsUnsupportedResourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parameter = builder.AddParameter("parameter");

        void ConfigureLifetime() => parameter.WithPersistentLifetime();

        var exception = Assert.Throws<InvalidOperationException>((Action)ConfigureLifetime);
        Assert.Contains("does not support lifetime configuration", exception.Message);
    }

    [Fact]
    public void WithPersistentLifetimeRemovesParentProcessLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithParentProcessLifetime(Environment.ProcessId)
            .WithPersistentLifetime();

        Assert.False(container.Resource.TryGetLastAnnotation<ParentProcessLifetimeAnnotation>(out _));
    }

    [Fact]
    public void WithSessionLifetimeRemovesParentProcessLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithParentProcessLifetime(Environment.ProcessId)
            .WithSessionLifetime();

        Assert.False(container.Resource.TryGetLastAnnotation<ParentProcessLifetimeAnnotation>(out _));
    }
}
