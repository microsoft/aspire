// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Net.Sockets;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task MultipleComputeEnvironments_ModelMutatedDuringValidation_DoesNotThrow()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/19266.
        //
        // 'validate-compute-environments' is only wired as RequiredBy 'before-start', so the task
        // DAG schedules it concurrently with every other step that shares that relationship —
        // including 'azure-prepare-resources', which appends role-assignment and identity resources
        // to the model. An append landing while validation enumerated model.Resources used to
        // invalidate the enumerator ("Collection was modified; enumeration operation may not
        // execute").
        //
        // Reproduce that interleaving deterministically rather than racing threads: the gate
        // resource mutates the model from inside its Annotations getter, which validation invokes
        // (via GetComputeEnvironment) while its enumerator over model.Resources is still in flight.
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        builder.AddResource(new TestComputeEnvironmentResource("env2"));

        var gate = new AnnotationGateComputeResource("api");
        builder.AddResource(gate).WithComputeEnvironment(env1);

        // Validation must pull at least one more element after the gate fires, otherwise the
        // mutation happens after enumeration has already finished and proves nothing.
        builder.AddResource(new TestComputeResource("tail")).WithComputeEnvironment(env1);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Arming from a step that validation is required by guarantees the gate is inert during
        // step resolution, which also reads Annotations, and armed by the time validation runs.
        builder.Pipeline.AddStep(
            "arm-model-mutation-gate",
            _ =>
            {
                gate.Arm(() => model.Resources.Add(new TestResource("added-during-validation")));
                return Task.CompletedTask;
            },
            requiredBy: WellKnownPipelineSteps.ValidateComputeEnvironments);

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();

        Assert.True(gate.Fired, "The gate never fired, so the mutation did not overlap validation.");
        Assert.Contains(model.Resources, resource => resource.Name == "added-during-validation");
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

    private sealed class TestResource(string name) : Resource(name)
    {
    }

    /// <summary>
    /// A compute resource whose <see cref="Annotations"/> getter runs a one-shot callback once armed.
    /// Compute-environment validation reads annotations (through <c>GetComputeEnvironment</c>) from
    /// inside its enumeration of <c>model.Resources</c>, so the callback fires while that enumerator
    /// is live. That makes it a deterministic stand-in for a concurrently scheduled pipeline step
    /// mutating the model mid-enumeration, with no reliance on thread timing.
    /// </summary>
    private sealed class AnnotationGateComputeResource(string name) : Resource(name), IComputeResource
    {
        private Action? _onAnnotationsAccessed;

        public bool Fired { get; private set; }

        public void Arm(Action onAnnotationsAccessed) => Volatile.Write(ref _onAnnotationsAccessed, onAnnotationsAccessed);

        public override ResourceAnnotationCollection Annotations
        {
            get
            {
                // Exchange so the callback runs exactly once even though annotations are read
                // repeatedly during application build and step resolution.
                if (Interlocked.Exchange(ref _onAnnotationsAccessed, null) is { } callback)
                {
                    Fired = true;
                    callback();
                }

                return base.Annotations;
            }
        }
    }
}
