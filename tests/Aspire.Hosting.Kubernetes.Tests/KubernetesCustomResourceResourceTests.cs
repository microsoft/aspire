// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesCustomResourceResourceTests
{
    [Fact]
    public void Constructor_WithValidParameters_CreatesResource()
    {
        var environment = new KubernetesEnvironmentResource("env");

        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Equal("my-resource", resource.Name);
        Assert.Same(environment, resource.Parent);
    }

    [Fact]
    public void Constructor_WithNullEnvironment_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new KubernetesCustomResourceResource("my-resource", null!));

        Assert.Equal("environment", ex.ParamName);
    }

    [Fact]
    public async Task ApiVersion_CanBeSet()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        resource.ApiVersion = ReferenceExpression.Create($"apps/v1");

        Assert.Equal("apps/v1", await resource.ApiVersion.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApiVersion_DefaultIsNull()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Null(await resource.ApiVersion.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kind_CanBeSet()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        resource.Kind = ReferenceExpression.Create($"Deployment");

        Assert.Equal("Deployment", await resource.Kind.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kind_DefaultIsNull()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Null(await resource.Kind.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Spec_DefaultIsNull()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Null(resource.Spec);
    }

    [Fact]
    public void Spec_CanBeSetToCustomObject()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        var spec = new GenericObjectSpec(new { replicas = 3, image = "nginx:latest" });
        resource.Spec = spec;

        Assert.Same(spec, resource.Spec);
    }

    [Fact]
    public void GeneratedResource_DefaultIsNull()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Null(resource.GeneratedResource);
    }

    [Fact]
    public void ImplementsIResourceWithParent()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.IsAssignableFrom<IResourceWithParent<KubernetesEnvironmentResource>>(resource);
    }

    [Fact]
    public void ImplementsResource()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.IsAssignableFrom<Resource>(resource);
    }
}
