// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROBES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "3")]
public class ResourceWithProbeTests
{
    [Fact]
    public void CreatesAnnotation()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var resource = appBuilder.AddResource(new CustomResourceWithProbes("myResouce"));
        resource.WithHttpsEndpoint();
        resource.WithHttpProbe(ProbeType.Startup, "/health");

        var annotations = resource.Resource.Annotations.OfType<ProbeAnnotation>().ToArray();

        Assert.Single(annotations);
    }

    [Fact]
    public void ProbesWithSameTypeReplace()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var resource = appBuilder.AddResource(new CustomResourceWithProbes("myResouce"));
        resource.WithHttpEndpoint();
        resource.WithHttpProbe(ProbeType.Readiness, "/health");
        resource.WithHttpProbe(ProbeType.Readiness, "/ready");

        var annotations = resource.Resource.Annotations.OfType<ProbeAnnotation>().ToArray();

        Assert.Single(annotations);

        var annotation = annotations[0];
        Assert.IsType<EndpointProbeAnnotation>(annotation);
        Assert.Equal("/ready", ((EndpointProbeAnnotation)annotation).Path);

    }

    [Fact]
    public void CreatesMultipleProbeTypes()
    {
        const string endpointName = "myEndpoint";
        var appBuilder = DistributedApplication.CreateBuilder();
        var resource = appBuilder.AddResource(new CustomResourceWithProbes("myResouce"));
        resource.WithHttpsEndpoint(8080, 8080, endpointName);
        resource.WithHttpProbe(ProbeType.Liveness, "/health", endpointName: endpointName);
        resource.WithHttpProbe(ProbeType.Readiness, "/ready", endpointName: endpointName);

        var annotations = resource.Resource.Annotations.OfType<ProbeAnnotation>().ToArray();

        Assert.Equal(2, annotations.Length);
    }

    private sealed class CustomResourceWithProbes(string name) : Resource(name), IResourceWithProbes, IResourceWithEndpoints
    {
    }
}
