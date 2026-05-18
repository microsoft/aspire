// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceBuilderLifetimeTests
{
    [Fact]
    public void WithPersistentLifetimeAddsLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithPersistentLifetime();

        var annotation = container.Resource.Annotations.OfType<LifetimeAnnotation>().Single();
        Assert.Equal(Lifetime.Persistent, annotation.Lifetime);
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
    public void WithParentProcessLifetimeReplacesExistingParentProcessLifetimeAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var originalTimestamp = new DateTime(2026, 5, 18, 1, 2, 3, DateTimeKind.Utc);
        var container = builder.AddContainer("container", "image")
            .WithAnnotation(new ParentProcessLifetimeAnnotation(parentProcessId: 1, parentProcessTimestamp: originalTimestamp));

        container.WithParentProcessLifetime(Environment.ProcessId);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<ParentProcessLifetimeAnnotation>());
        Assert.Equal(Environment.ProcessId, annotation.ParentProcessId);
        Assert.NotEqual(originalTimestamp, annotation.ParentProcessTimestamp);
    }

    [Fact]
    public void WithLifetimeOfMatchesSourceResourceLifetime()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var source = builder.AddContainer("source", "image")
            .WithPersistentLifetime();
        var container = builder.AddContainer("container", "image")
            .WithSessionLifetime()
            .WithLifetimeOf(source);

        Assert.Equal(Lifetime.Persistent, container.Resource.GetLifetimeType());
        Assert.Empty(container.Resource.Annotations.OfType<LifetimeAnnotation>());

        source.WithSessionLifetime();

        Assert.Equal(Lifetime.Session, container.Resource.GetLifetimeType());
    }

    [Fact]
    public void WithLifetimeOfMatchesSourceParentProcessLifetime()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var parentProcess = Process.GetCurrentProcess();
        var parentProcessIdentity = DcpProcessMonitor.GetMonitorProcessIdentity(parentProcess);

        var source = builder.AddContainer("source", "image")
            .WithParentProcessLifetime(parentProcess.Id);
        var container = builder.AddContainer("container", "image")
            .WithLifetimeOf(source);

        Assert.True(container.Resource.TryGetParentProcessLifetime(out var parentProcessLifetimeAnnotation));
        Assert.Equal(parentProcessIdentity.ProcessId, parentProcessLifetimeAnnotation.ParentProcessId);
        Assert.Equal(parentProcessIdentity.Timestamp, parentProcessLifetimeAnnotation.ParentProcessTimestamp);

        source.WithSessionLifetime();

        Assert.False(container.Resource.TryGetParentProcessLifetime(out _));
    }

    [Fact]
    public void ExplicitLifetimeOverridesWithLifetimeOf()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var source = builder.AddContainer("source", "image")
            .WithSessionLifetime();
        var container = builder.AddContainer("container", "image")
            .WithLifetimeOf(source)
            .WithPersistentLifetime();

        source.WithSessionLifetime();

        Assert.Equal(Lifetime.Persistent, container.Resource.GetLifetimeType());
    }

    [Fact]
    public void WithLifetimeOfRejectsUnsupportedResourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var parameter = builder.AddParameter("parameter");
        var container = builder.AddContainer("container", "image");

        void ConfigureLifetime() => parameter.WithLifetimeOf(container);

        var exception = Assert.Throws<InvalidOperationException>((Action)ConfigureLifetime);
        Assert.Contains("does not support lifetime configuration", exception.Message);
    }

    [Fact]
    public void WithLifetimeOfDetectsCircularReferences()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var containerA = builder.AddContainer("container-a", "image");
        var containerB = builder.AddContainer("container-b", "image")
            .WithLifetimeOf(containerA);
        containerA.WithLifetimeOf(containerB);

        var exception = Assert.Throws<InvalidOperationException>(() => containerA.Resource.GetLifetimeType());
        Assert.Contains("circular lifetime reference", exception.Message);
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
