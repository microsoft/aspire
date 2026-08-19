// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

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

        // The Radius Kubernetes container recipe names the Service `{name}-{name}`
        // (${normalizedName}-${containerName}, both = the resource name), so service discovery must
        // address that Service. The namespace segment is required for the FQDN to resolve across
        // namespaces; the default environment namespace is "default".
        Assert.Equal("my-service-my-service.default.svc.cluster.local", expression.ValueExpression);
    }

    [Theory]
    [InlineData(EndpointProperty.Url, "http://api-api.default.svc.cluster.local:8080")]
    [InlineData(EndpointProperty.Host, "api-api.default.svc.cluster.local")]
    [InlineData(EndpointProperty.IPV4Host, "api-api.default.svc.cluster.local")]
    [InlineData(EndpointProperty.HostAndPort, "api-api.default.svc.cluster.local:8080")]
    [InlineData(EndpointProperty.Port, "8080")]
    [InlineData(EndpointProperty.TargetPort, "8080")]
    [InlineData(EndpointProperty.Scheme, "http")]
    public void GetEndpointPropertyExpression_UsesServiceNameAndContainerPort(EndpointProperty property, string expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("radius");
        var api = builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        var endpoint = api.GetEndpoint("http");

        var expression = ((IComputeEnvironmentResource)radiusEnv)
            .GetEndpointPropertyExpression(endpoint.Property(property));

        // The recipe Service is `{name}-{name}` and listens on the container port (8080), so the
        // Url/Host/Port all target that Service and port rather than the bare name / port 80.
        Assert.Equal(expected, expression.ValueExpression);
    }

    /// <summary>
    /// The transport-security guard the Radius publisher applies has to hold on this path too. A
    /// Kubernetes, Azure Container Apps, or App Service consumer of a Radius-owned backing resource
    /// is routed here by <c>ComputeEnvironmentEndpointResolver</c>, and no mapped Radius type
    /// publishes a transport-security output — so answering from the local endpoint declaration
    /// would describe how the resource runs locally rather than how its recipe deploys it.
    /// </summary>
    [Theory]
    [InlineData(EndpointProperty.Scheme)]
    [InlineData(EndpointProperty.TlsEnabled)]
    [InlineData(EndpointProperty.Url)]
    public void GetEndpointPropertyExpression_TlsEnabledBackingResource_Throws(EndpointProperty property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        var cache = builder.AddRedis("cache").WithEndpoint("tcp", e => e.TlsEnabled = true);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        var endpoint = cache.GetEndpoint("tcp");

        var ex = Assert.Throws<RadiusBackingResourceEndpointException>(() => ((IComputeEnvironmentResource)radiusEnv)
            .GetEndpointPropertyExpression(endpoint.Property(property)));

        Assert.Contains("ASPIRERADIUS081", ex.Message, StringComparison.Ordinal);
        Assert.Equal("cache", ex.Resource.Name);
    }

    /// <summary>
    /// The guard is scoped to transport security on a backing resource, so it must not fire for a
    /// plain container endpoint — those answer their scheme correctly on this path and did so
    /// before backing-resource projection existed.
    /// </summary>
    [Fact]
    public void GetEndpointPropertyExpression_TlsEnabledContainerEndpoint_IsAnswered()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        var api = builder.AddContainer("api", "myapp/api", "latest")
            .WithHttpsEndpoint(targetPort: 8443, name: "https");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        var endpoint = api.GetEndpoint("https");

        var expression = ((IComputeEnvironmentResource)radiusEnv)
            .GetEndpointPropertyExpression(endpoint.Property(EndpointProperty.Scheme));

        Assert.Equal("https", expression.ValueExpression);
    }

    /// <summary>
    /// A backing resource without TLS is unaffected: the recipe's transport is what the endpoint
    /// already describes, so there is nothing to disagree about.
    /// </summary>
    [Fact]
    public void GetEndpointPropertyExpression_BackingResourceWithoutTls_AnswersScheme()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        var cache = builder.AddRedis("cache");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        var endpoint = cache.GetEndpoint("tcp");

        var expression = ((IComputeEnvironmentResource)radiusEnv)
            .GetEndpointPropertyExpression(endpoint.Property(EndpointProperty.Scheme));

        Assert.Equal("redis", expression.ValueExpression);
    }
}
