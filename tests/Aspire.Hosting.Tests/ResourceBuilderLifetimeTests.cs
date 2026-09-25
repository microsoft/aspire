// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001 // Resource lifetime APIs are experimental.

using System.Diagnostics;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceBuilderLifetimeTests
{
    [Fact]
    public void WithPersistentLifetimeAddsPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithPersistentLifetime();

        var annotation = container.Resource.Annotations.OfType<PersistenceAnnotation>().Single();
        Assert.Equal(PersistenceMode.Persistent, annotation.Mode);
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
    public void WithPersistentLifetimeReplacesPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithParentProcessLifetime(Environment.ProcessId)
            .WithPersistentLifetime();

        var annotation = container.Resource.Annotations.OfType<PersistenceAnnotation>().Single();
        Assert.Equal(PersistenceMode.Persistent, annotation.Mode);
        Assert.Null(annotation.ParentProcessId);
        Assert.Null(annotation.ParentProcessTimestamp);
    }

    [Fact]
    public void WithParentProcessLifetimeReplacesExistingPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithPersistentLifetime();

        container.WithParentProcessLifetime(Environment.ProcessId);

        var annotation = Assert.Single(container.Resource.Annotations.OfType<PersistenceAnnotation>());
        Assert.Equal(PersistenceMode.ParentProcess, annotation.Mode);
        Assert.Equal(Environment.ProcessId, annotation.ParentProcessId);
        Assert.NotNull(annotation.ParentProcessTimestamp);
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
        var annotation = Assert.Single(container.Resource.Annotations.OfType<PersistenceAnnotation>());
        Assert.Equal(PersistenceMode.Resource, annotation.Mode);
        Assert.Same(source.Resource, annotation.SourceResource);

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

        Assert.True(container.Resource.TryGetParentProcessLifetime(out var parentProcessId, out var parentProcessTimestamp));
        Assert.Equal(parentProcessIdentity.ProcessId, parentProcessId);
        Assert.Equal(parentProcessIdentity.Timestamp, parentProcessTimestamp);

        source.WithSessionLifetime();

        Assert.False(container.Resource.TryGetParentProcessLifetime(out _, out _));
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
    public void WithSessionLifetimeReplacesPersistenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithParentProcessLifetime(Environment.ProcessId)
            .WithSessionLifetime();

        var annotation = container.Resource.Annotations.OfType<PersistenceAnnotation>().Single();
        Assert.Equal(PersistenceMode.Session, annotation.Mode);
        Assert.Null(annotation.ParentProcessId);
        Assert.Null(annotation.ParentProcessTimestamp);
    }
}
