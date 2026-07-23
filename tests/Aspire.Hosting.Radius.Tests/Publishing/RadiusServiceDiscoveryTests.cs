// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using System.Net.Sockets;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class RadiusServiceDiscoveryTests
{
    [Fact]
    public void GetServiceName_DoublesResourceName()
    {
        var resource = new ContainerResource("apiservice");

        Assert.Equal("apiservice-apiservice", RadiusServiceDiscovery.GetServiceName(resource));
    }

    [Fact]
    public void ResolveServicePort_PrefersTargetPort()
    {
        var resource = new ContainerResource("api");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http", port: 5000, targetPort: 8080));

        Assert.Equal(8080, RadiusServiceDiscovery.ResolveServicePort(resource, "http"));
    }

    [Fact]
    public void ResolveServicePort_ContainerUsesHostPortAsContainerPort()
    {
        // A container with only a host port listens on that port (ResolveEndpoints treats it as the
        // implicit container port).
        var resource = new ContainerResource("api");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http", port: 5000));

        Assert.Equal(5000, RadiusServiceDiscovery.ResolveServicePort(resource, "http"));
    }

    [Fact]
    public void ResolveServicePort_DefaultsProjectDefaultHttpToStandardContainerPort()
    {
        // A project's default HTTP endpoint has no resolved port at publish time (the deployment
        // tool would assign one); it must still get a container port so the recipe creates a Service.
        var resource = new ProjectResource("webapp");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http"));

        Assert.Equal(RadiusServiceDiscovery.DefaultProjectContainerPort, RadiusServiceDiscovery.ResolveServicePort(resource, "http"));
    }

    [Fact]
    public void ResolveServicePort_DefaultsExplicitPortlessHttpsToContainerPort()
    {
        // An explicit portless HTTPS endpoint (not the synthetic default) must still get a container
        // port so the recipe creates a Service — matching the Kubernetes publisher's 8080 default.
        var resource = new ProjectResource("webapp");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https"));

        Assert.Equal(RadiusServiceDiscovery.DefaultProjectContainerPort, RadiusServiceDiscovery.ResolveServicePort(resource, "https"));
    }

    [Fact]
    public void ResolveServicePort_SkipsSyntheticDefaultHttpsEndpoint()
    {
        // The synthetic default HTTPS endpoint is skipped: containers don't terminate TLS in-cluster
        // and the framework reuses the HTTP port for it, so no separate Service is created.
        var resource = new ProjectResource("webapp");
        var https = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https");
        resource.Annotations.Add(https);
        ((IProjectLaunchDefaultsResource)resource).DefaultHttpsEndpoint = https;

        Assert.Null(RadiusServiceDiscovery.ResolveServicePort(resource, "https"));
    }

    [Fact]
    public void ResolveServicePort_AllocatesDistinctPortsForMultiplePortlessEndpoints()
    {
        // Multiple portless endpoints must not collapse onto the same Service port; ResolveEndpoints
        // allocates a distinct port for each so the recipe emits a valid multi-port Service.
        var resource = new ContainerResource("api");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "tcp", name: "one"));
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "tcp", name: "two"));

        var first = RadiusServiceDiscovery.ResolveServicePort(resource, "one");
        var second = RadiusServiceDiscovery.ResolveServicePort(resource, "two");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }
}
