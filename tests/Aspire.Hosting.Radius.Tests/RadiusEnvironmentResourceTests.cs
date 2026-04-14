// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius.Tests;

public class RadiusEnvironmentResourceTests
{
    [Fact]
    public void Constructor_SetsName()
    {
        var resource = new RadiusEnvironmentResource("my-radius");

        Assert.Equal("my-radius", resource.Name);
    }

    [Fact]
    public void Namespace_DefaultsToDefault()
    {
        var resource = new RadiusEnvironmentResource("radius");

        Assert.Equal("default", resource.Namespace);
    }

    [Fact]
    public void Namespace_CanBeSet()
    {
        var resource = new RadiusEnvironmentResource("radius")
        {
            Namespace = "staging-ns"
        };

        Assert.Equal("staging-ns", resource.Namespace);
    }

    [Fact]
    public void ImplementsIComputeEnvironmentResource()
    {
        var resource = new RadiusEnvironmentResource("radius");

        Assert.IsAssignableFrom<IComputeEnvironmentResource>(resource);
    }

    [Fact]
    public void ImplementsIResource()
    {
        var resource = new RadiusEnvironmentResource("radius");

        Assert.IsAssignableFrom<IResource>(resource);
    }

    [Fact]
    public void GetHostAddressExpression_ReturnsKubernetesDns()
    {
        var environment = new RadiusEnvironmentResource("radius");
        var container = new ContainerResource("my-service");
        var endpoint = new EndpointReference(container, "http");

        var expression = ((IComputeEnvironmentResource)environment).GetHostAddressExpression(endpoint);

        Assert.Equal("my-service.svc.cluster.local", expression.ValueExpression);
    }
}
