// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

public class ComputeEnvironmentEndpointResolverTests
{
    [Fact]
    public async Task OwningResourceInDifferentEnvironment_DelegatesToOwningEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        var owningEnv = builder.AddResource(new TestComputeEnvironmentResource("owning"));
        var agent = builder.AddResource(new TestComputeResource("agent"));
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(owningEnv.Resource) { ComputeEnvironment = owningEnv.Resource });

        var resolved = ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
            endpoint.Property(EndpointProperty.Url), out var expression, currentEnv.Resource);

        Assert.True(resolved);
        Assert.NotNull(expression);
        // The owning environment (TestComputeEnvironmentResource) maps the host to "{name}.example.com".
        Assert.Equal("http://agent.example.com:8080", await expression.GetValueAsync(default).DefaultTimeout());
    }

    [Fact]
    public void OwningResourceDeploysToCurrentEnvironment_ReturnsFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        var agent = builder.AddResource(new TestComputeResource("agent"));
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(currentEnv.Resource) { ComputeEnvironment = currentEnv.Resource });

        var resolved = ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
            endpoint.Property(EndpointProperty.Url), out var expression, currentEnv.Resource);

        Assert.False(resolved);
        Assert.Null(expression);
    }

    [Fact]
    public void OwningResourceBoundToCurrentEnvironmentWithoutDeploymentTarget_ReturnsFalseViaBackstop()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        // Bind via WithComputeEnvironment only (no DeploymentTargetAnnotation). The fast-path loop
        // falls through and the ReferenceEquals backstop catches it after the effective environment
        // resolves to the current environment.
        var agent = builder.AddResource(new TestComputeResource("agent"))
            .WithComputeEnvironment(currentEnv);
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);

        var resolved = ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
            endpoint.Property(EndpointProperty.Url), out var expression, currentEnv.Resource);

        Assert.False(resolved);
        Assert.Null(expression);
    }

    [Fact]
    public void OwningResourceHasNoComputeEnvironment_ReturnsFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        // No binding and no deployment target: nothing to delegate to.
        var agent = builder.AddResource(new TestComputeResource("agent"));
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);

        var resolved = ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
            endpoint.Property(EndpointProperty.Url), out var expression, currentEnv.Resource);

        Assert.False(resolved);
        Assert.Null(expression);
    }

    [Fact]
    public void OwningResourceBoundToCurrentEnvironmentWithMultipleDeploymentTargets_ReturnsFalseAndDoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        var otherEnv = builder.AddResource(new TestComputeEnvironmentResource("other"));
        // Explicit binding to the current environment plus deployment targets for both environments.
        // The binding makes GetDeploymentTargetAnnotation(current) select the current target without
        // throwing on the multi-target ambiguity.
        var agent = builder.AddResource(new TestComputeResource("agent"))
            .WithComputeEnvironment(currentEnv);
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(currentEnv.Resource) { ComputeEnvironment = currentEnv.Resource });
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(otherEnv.Resource) { ComputeEnvironment = otherEnv.Resource });
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);

        var resolved = ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
            endpoint.Property(EndpointProperty.Url), out var expression, currentEnv.Resource);

        Assert.False(resolved);
        Assert.Null(expression);
    }

    [Fact]
    public void OwningResourceUnboundWithMultipleDeploymentTargets_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var currentEnv = builder.AddResource(new TestComputeEnvironmentResource("current"));
        var otherEnv = builder.AddResource(new TestComputeEnvironmentResource("other"));
        // No binding: an unbound resource with more than one deployment target is ambiguous.
        // GetDeploymentTargetAnnotation(current) throws, exactly like the parameterless overload used
        // by TryGetEffectiveComputeEnvironment. This documents that the fast-path loop is NOT a guard
        // against this throw; the pipeline rejects this configuration earlier in practice.
        var agent = builder.AddResource(new TestComputeResource("agent"));
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(currentEnv.Resource) { ComputeEnvironment = currentEnv.Resource });
        agent.Resource.Annotations.Add(new DeploymentTargetAnnotation(otherEnv.Resource) { ComputeEnvironment = otherEnv.Resource });
        var endpoint = AddHttpEndpoint(agent.Resource, port: 8080, targetPort: 5000);

        Assert.Throws<InvalidOperationException>(() =>
            ComputeEnvironmentEndpointResolver.TryGetCrossEnvironmentEndpointExpression(
                endpoint.Property(EndpointProperty.Url), out _, currentEnv.Resource));
    }

    private static EndpointReference AddHttpEndpoint(TestComputeResource resource, int? port, int? targetPort)
    {
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "http", name: "http", port: port, targetPort: targetPort);
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
