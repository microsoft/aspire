// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for deploying a single compute resource to several compute environments as regional stamps.
/// </summary>
[Trait("Partition", "6")]
public class ComputeStampTests
{
    [Fact]
    public void UnboundResourceHasNoStamps()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var api = builder.AddResource(new TestComputeResource("api"));

        Assert.Empty(api.Resource.GetComputeStamps());
        Assert.False(api.Resource.IsStamped());
    }

    [Fact]
    public void SingleComputeEnvironmentProducesOneStampThatDoesNotQualifyNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env);

        var stamp = Assert.Single(api.Resource.GetComputeStamps());

        Assert.Same(env.Resource, stamp.Environment);
        Assert.Equal("env1", stamp.Name);
        Assert.False(stamp.QualifiesNames);
        Assert.False(api.Resource.IsStamped());

        // The load-bearing invariant: a single-environment resource keeps its plain name so already
        // deployed infrastructure is never renamed.
        Assert.Equal("api", api.Resource.GetStampQualifiedName(env.Resource));
    }

    [Fact]
    public void WithComputeEnvironmentsProducesOneStampPerEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        Assert.True(api.Resource.IsStamped());

        Assert.Collection(
            api.Resource.GetComputeStamps(),
            stamp =>
            {
                Assert.Same(env1.Resource, stamp.Environment);
                Assert.Equal("env1", stamp.Name);
                Assert.True(stamp.QualifiesNames);
            },
            stamp =>
            {
                Assert.Same(env2.Resource, stamp.Environment);
                Assert.Equal("env2", stamp.Name);
                Assert.True(stamp.QualifiesNames);
            });

        Assert.Equal("api-env1", api.Resource.GetStampQualifiedName(env1.Resource));
        Assert.Equal("api-env2", api.Resource.GetStampQualifiedName(env2.Resource));
    }

    [Fact]
    public void WithStampUsesTheSuppliedStampNameAndAlwaysQualifiesNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("aca-eastus"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithStamp(env1, "eus");

        var stamp = Assert.Single(api.Resource.GetComputeStamps());

        Assert.Equal("eus", stamp.Name);
        Assert.True(stamp.QualifiesNames);
        Assert.Equal("api-eus", api.Resource.GetStampQualifiedName(env1.Resource));
    }

    [Fact]
    public void GetStampQualifiedNameReturnsResourceNameForUnknownEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env1);

        Assert.Equal("api", api.Resource.GetStampQualifiedName(env2.Resource));
        Assert.Equal("api", api.Resource.GetStampQualifiedName(null));
    }

    [Fact]
    public void BindingTheSameComputeEnvironmentTwiceThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1);

        var ex = Assert.Throws<InvalidOperationException>(() => api.WithComputeEnvironments(env1));

        Assert.Contains("'api'", ex.Message);
        Assert.Contains("'env1'", ex.Message);
    }

    [Fact]
    public void RepeatingTheSingularWithComputeEnvironmentKeepsOneUnqualifiedStamp()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env1)
            .WithComputeEnvironment(env1);

        // Repeating the singular binding must not look like two stamps, otherwise generated names would gain
        // a suffix and already deployed infrastructure would be recreated.
        var stamp = Assert.Single(api.Resource.GetComputeStamps());
        Assert.Same(env1.Resource, stamp.Environment);
        Assert.False(stamp.QualifiesNames);
        Assert.False(api.Resource.IsStamped());
        Assert.Equal("api", api.Resource.GetStampQualifiedName(env1.Resource));
    }

    [Fact]
    public void SingularWithComputeEnvironmentIsLastOneWins()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironment(env1)
            .WithComputeEnvironment(env2);

        // The singular API meant "last one wins" before stamping existed. Turning a second call into a second
        // stamp would silently double the deployment and rename the first one.
        var stamp = Assert.Single(api.Resource.GetComputeStamps());
        Assert.Same(env2.Resource, stamp.Environment);
        Assert.False(api.Resource.IsStamped());
        Assert.False(api.Resource.IsBoundToComputeEnvironment(env1.Resource));
        Assert.True(api.Resource.IsBoundToComputeEnvironment(env2.Resource));
        Assert.Equal("api", api.Resource.GetStampQualifiedName(env2.Resource));
    }

    [Fact]
    public void GetContainerRegistryThrowsWhenStampsUseDifferentRegistries()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t1"))
        {
            ComputeEnvironment = env1.Resource,
            ContainerRegistry = new TestContainerRegistry("acr1")
        });
        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t2"))
        {
            ComputeEnvironment = env2.Resource,
            ContainerRegistry = new TestContainerRegistry("acr2")
        });

        // The image is built and pushed once, so every stamp has to be able to pull it from the same
        // registry. Otherwise every stamp after the first cannot pull its image.
        var ex = Assert.Throws<InvalidOperationException>(api.Resource.GetContainerRegistry);

        Assert.Contains("'api'", ex.Message);
        Assert.Contains("acr1", ex.Message);
        Assert.Contains("acr2", ex.Message);
        Assert.Contains("WithContainerRegistry", ex.Message);
    }

    [Fact]
    public void GetContainerRegistryReturnsTheSharedRegistryAcrossStamps()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        var shared = new TestContainerRegistry("shared-acr");
        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t1")) { ComputeEnvironment = env1.Resource, ContainerRegistry = shared });
        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t2")) { ComputeEnvironment = env2.Resource, ContainerRegistry = shared });

        Assert.Same(shared, api.Resource.GetContainerRegistry());
    }

    [Fact]
    public void WithComputeEnvironmentsRequiresAtLeastOneEnvironment()
    {        using var builder = TestDistributedApplicationBuilder.Create();

        var api = builder.AddResource(new TestComputeResource("api"));

        Assert.Throws<ArgumentException>(() => api.WithComputeEnvironments());
    }

    [Fact]
    public void IsBoundToComputeEnvironmentMatchesEveryStamp()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var env3 = builder.AddResource(new TestComputeEnvironmentResource("env3"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        Assert.True(api.Resource.IsBoundToComputeEnvironment(env1.Resource));
        Assert.True(api.Resource.IsBoundToComputeEnvironment(env2.Resource));
        Assert.False(api.Resource.IsBoundToComputeEnvironment(env3.Resource));
    }

    [Fact]
    public async Task StampedResourceSatisfiesTheMultipleComputeEnvironmentValidation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default).DefaultTimeout();
    }

    [Fact]
    public void GetDeploymentTargetAnnotationsReturnsEveryStampTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("api-env1-target")) { ComputeEnvironment = env1.Resource });
        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("api-env2-target")) { ComputeEnvironment = env2.Resource });

        Assert.Equal(2, api.Resource.GetDeploymentTargetAnnotations().Count);

        // Narrowing by environment picks exactly one stamp.
        Assert.Equal("api-env1-target", api.Resource.GetDeploymentTargetAnnotation(env1.Resource)!.DeploymentTarget.Name);
        Assert.Equal("api-env2-target", api.Resource.GetDeploymentTargetAnnotation(env2.Resource)!.DeploymentTarget.Name);
    }

    [Fact]
    public void GetDeploymentTargetAnnotationThrowsForAStampedResourceWithoutAnEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var env1 = builder.AddResource(new TestComputeEnvironmentResource("env1"));
        var env2 = builder.AddResource(new TestComputeEnvironmentResource("env2"));
        var api = builder.AddResource(new TestComputeResource("api"))
            .WithComputeEnvironments(env1, env2);

        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t1")) { ComputeEnvironment = env1.Resource });
        api.Resource.Annotations.Add(new DeploymentTargetAnnotation(new TestComputeResource("t2")) { ComputeEnvironment = env2.Resource });

        var ex = Assert.Throws<InvalidOperationException>(() => api.Resource.GetDeploymentTargetAnnotation());

        Assert.Contains("deployed as multiple stamps", ex.Message);
        Assert.Contains("GetDeploymentTargetAnnotations", ex.Message);
    }

    private sealed class TestContainerRegistry : Resource, IContainerRegistry
    {
        public TestContainerRegistry(string registryName) : base(registryName)
        {
        }

        ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{Name}");

        public ReferenceExpression Endpoint => ReferenceExpression.Create($"{Name}.azurecr.io");
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
