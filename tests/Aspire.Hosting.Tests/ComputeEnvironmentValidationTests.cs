// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class ComputeEnvironmentValidationTests
{
    [Fact]
    public async Task MultipleComputeEnvironments_WithUnboundComputeResource_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddResource(new TestComputeEnvironmentResource("env1"));
        builder.AddResource(new TestComputeEnvironmentResource("env2"));
        builder.AddResource(new TestComputeResource("api"));

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Contains("'api'", ex.Message);
        Assert.Contains("'env1'", ex.Message);
        Assert.Contains("'env2'", ex.Message);
        Assert.Contains("WithComputeEnvironment", ex.Message);
    }

    [Fact]
    public async Task MultipleComputeEnvironments_WithAllResourcesBound_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        builder.AddResource(new TestComputeEnvironmentResource("env2"));
        builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env1);

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();
    }

    [Fact]
    public async Task SingleComputeEnvironment_AutoBindsUnboundResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var api = builder.AddResource(new TestComputeResource("api"));

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        Assert.Same(env.Resource, api.Resource.GetComputeEnvironment());
    }

    [Theory]
    [InlineData(EndpointProperty.Url, "http://api.example.com:8080")]
    [InlineData(EndpointProperty.Host, "api.example.com")]
    [InlineData(EndpointProperty.IPV4Host, "api.example.com")]
    [InlineData(EndpointProperty.Port, "8080")]
    [InlineData(EndpointProperty.TargetPort, "5000")]
    [InlineData(EndpointProperty.Scheme, "http")]
    [InlineData(EndpointProperty.HostAndPort, "api.example.com:8080")]
    [InlineData(EndpointProperty.TlsEnabled, "False")]
    public async Task GetEndpointPropertyExpression_ReturnsDefaultEndpointPropertyExpression(EndpointProperty property, string expected)
    {
        IComputeEnvironmentResource environment = new TestComputeEnvironmentResource("env");
        var endpointReference = CreateEndpointReference("http", port: 8080, targetPort: 5000);

#pragma warning disable ASPIRECOMPUTE002
        var expression = environment.GetEndpointPropertyExpression(endpointReference.Property(property));
#pragma warning restore ASPIRECOMPUTE002

        Assert.Equal(expected, await expression.GetValueAsync(default));
    }

    [Fact]
    public void GetEndpointPropertyExpression_ThrowsWhenCustomSchemeDoesNotSpecifyPort()
    {
        IComputeEnvironmentResource environment = new TestComputeEnvironmentResource("env");
        var endpointReference = CreateEndpointReference("redis", port: null, targetPort: 6379);

#pragma warning disable ASPIRECOMPUTE002
        var ex = Assert.Throws<InvalidOperationException>(() => environment.GetEndpointPropertyExpression(endpointReference.Property(EndpointProperty.Url)));
#pragma warning restore ASPIRECOMPUTE002

        Assert.Contains("Endpoint 'redis' must specify a port for scheme 'redis'.", ex.Message);
    }

    private static EndpointReference CreateEndpointReference(string uriScheme, int? port, int? targetPort)
    {
        var resource = new TestComputeResource("api");
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: uriScheme, name: uriScheme, port: port, targetPort: targetPort);
        resource.Annotations.Add(endpoint);

        return new EndpointReference(resource, endpoint);
    }

    private sealed class TestComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource
    {
#pragma warning disable ASPIRECOMPUTE002
        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
            ReferenceExpression.Create($"{endpointReference.Resource.Name}.example.com");
#pragma warning restore ASPIRECOMPUTE002
    }

    private sealed class TestComputeResource(string name) : Resource(name), IComputeResource, IResourceWithEndpoints
    {
    }
}
