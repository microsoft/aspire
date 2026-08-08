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

    [Fact]
    public async Task ExplicitOnlyEnvironment_DoesNotDisableAnotherEnvironmentsImplicitBinding()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var defaultEnvironment = builder.AddResource(new TestComputeEnvironmentResource("default"));
        var explicitEnvironment = builder.AddResource(new TestComputeEnvironmentResource(
            "explicit",
            allowsImplicitBinding: false));
        var explicitlyBoundResource = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(explicitEnvironment);
        var implicitlyBoundResource = builder.AddResource(new TestComputeResource("worker"));

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        Assert.Same(explicitEnvironment.Resource, explicitlyBoundResource.Resource.GetComputeEnvironment());
        Assert.Same(defaultEnvironment.Resource, implicitlyBoundResource.Resource.GetComputeEnvironment());
    }

    [Fact]
    public async Task ExplicitOnlyComputeEnvironment_RejectsUnboundResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource("env1", allowsImplicitBinding: false));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env);
        var worker = builder.AddResource(new TestComputeResource("worker"));

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Same(env.Resource, api.Resource.GetComputeEnvironment());
        Assert.Null(worker.Resource.GetComputeEnvironment());
        Assert.Equal(
            "Compute environment 'env1' does not allow implicit binding, but compute resource(s) 'worker' are not bound to an environment. " +
            "Bind each resource by calling 'WithComputeEnvironment' on its resource builder.",
            ex.Message);
    }

    [Fact]
    public async Task ExplicitOnlyComputeEnvironment_IgnoresPublishExcludedUnboundResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddResource(new TestComputeEnvironmentResource(
            "env",
            allowsImplicitBinding: false,
            minimumResourceCount: 1,
            maximumResourceCount: 1));
        builder.AddResource(new TestComputeResource("api")).WithComputeEnvironment(env);
        builder.AddResource(new TestComputeResource("worker")).ExcludeFromManifest();

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();
    }

    [Fact]
    public async Task ComputeEnvironment_WithTooFewBoundResources_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddResource(new TestComputeEnvironmentResource(
            "env",
            allowsImplicitBinding: false,
            minimumResourceCount: 1,
            maximumResourceCount: 1));

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Equal(
            "Compute environment 'env' requires at least 1 bound compute resource(s), but 0 were found. " +
            "Bind a resource by calling 'WithComputeEnvironment' on its resource builder.",
            ex.Message);
    }

    [Fact]
    public async Task ComputeEnvironment_WithTooManyBoundResources_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource(
            "env",
            allowsImplicitBinding: false,
            minimumResourceCount: 1,
            maximumResourceCount: 1));
        builder.AddResource(new TestComputeResource("api")).WithComputeEnvironment(env);
        builder.AddResource(new TestComputeResource("worker")).WithComputeEnvironment(env);

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Equal(
            "Compute environment 'env' supports at most 1 bound compute resource(s), but 2 were found.",
            ex.Message);
    }

    [Fact]
    public async Task ComputeEnvironment_WithUnsupportedBoundResource_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource(
            "env",
            allowsImplicitBinding: false,
            supportsResource: _ => false));
        builder.AddResource(new TestComputeResource("api")).WithComputeEnvironment(env);

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Equal(
            "Compute environment 'env' does not support compute resource(s) 'api (TestComputeResource)'.",
            ex.Message);
    }

    [Theory]
    [InlineData(-1, null)]
    [InlineData(0, -1)]
    [InlineData(2, 1)]
    public async Task ComputeEnvironment_WithInvalidResourceCountPolicy_Throws(int minimumResourceCount, int? maximumResourceCount)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddResource(new TestComputeEnvironmentResource(
            "env",
            minimumResourceCount: minimumResourceCount,
            maximumResourceCount: maximumResourceCount));

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(default)).DefaultTimeout();

        Assert.Equal(
            "Compute environment 'env' has an invalid resource count policy. " +
            "The minimum count must be non-negative and cannot exceed the maximum count.",
            ex.Message);
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

    private sealed class TestComputeEnvironmentResource(
        string name,
        bool allowsImplicitBinding = true,
        int minimumResourceCount = 0,
        int? maximumResourceCount = null,
        Func<IComputeResource, bool>? supportsResource = null) : Resource(name), IComputeEnvironmentResource
    {
#pragma warning disable ASPIRECOMPUTE002
        public bool AllowsImplicitBinding => allowsImplicitBinding;

        public int MinimumResourceCount => minimumResourceCount;

        public int? MaximumResourceCount => maximumResourceCount;

        public bool SupportsResource(IComputeResource resource) => supportsResource?.Invoke(resource) ?? true;

        public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) =>
            ReferenceExpression.Create($"{endpointReference.Resource.Name}.example.com");
#pragma warning restore ASPIRECOMPUTE002
    }

    private sealed class TestComputeResource(string name) : Resource(name), IComputeResource, IResourceWithEndpoints
    {
    }
}
