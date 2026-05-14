// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;

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
    public void ApiVersion_CanBeSet()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        resource.ApiVersion = "apps/v1";

        Assert.Equal("apps/v1", resource.ApiVersion);
    }

    [Fact]
    public void ApiVersion_DefaultIsEmpty()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Equal("", resource.ApiVersion);
    }

    [Fact]
    public void Kind_CanBeSet()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        resource.Kind = "Deployment";

        Assert.Equal("Deployment", resource.Kind);
    }

    [Fact]
    public void Kind_DefaultIsEmpty()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.Equal("", resource.Kind);
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
    public void Build_ReturnsCustomResourceV1()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment)
        {
            ApiVersion = "v1",
            Kind = "ConfigMap"
        };

        var result = resource.Build();

        Assert.NotNull(result);
        Assert.IsType<CustomResourceV1>(result);
    }

    [Fact]
    public void Build_SetsApiVersionAndKind()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment)
        {
            ApiVersion = "apps/v1",
            Kind = "Deployment"
        };

        var result = (CustomResourceV1)resource.Build();

        Assert.Equal("apps/v1", result.ApiVersion);
        Assert.Equal("Deployment", result.Kind);
    }

    [Fact]
    public void Build_SetsMetadataName()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        var result = (CustomResourceV1)resource.Build();

        Assert.Equal("my-resource", result.Metadata.Name);
    }

    [Fact]
    public void Build_ConvertsResourceNameToKubernetes()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("MyResource", environment);

        var result = (CustomResourceV1)resource.Build();

        // ToKubernetesResourceName converts to lowercase with hyphens
        Assert.Equal("myresource", result.Metadata.Name);
    }

    [Fact]
    public void Build_CopiesSpec()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var spec = new GenericObjectSpec(new { replicas = 5, image = "nginx:latest" });
        var resource = new KubernetesCustomResourceResource("my-resource", environment)
        {
            Spec = spec
        };

        var result = (CustomResourceV1)resource.Build();

        Assert.Same(spec, result.Spec);
    }

    [Fact]
    public void Build_SetsGeneratedResourceProperty()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        var result = resource.Build();

        Assert.NotNull(resource.GeneratedResource);
        Assert.Same(result, resource.GeneratedResource);
    }

    [Fact]
    public void Build_CalledMultipleTimes_CreatesNewResourceEachTime()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment)
        {
            ApiVersion = "v1",
            Kind = "ConfigMap"
        };

        var result1 = resource.Build();
        var result2 = resource.Build();

        Assert.NotEqual(result1, result2);
        Assert.Same(result2, resource.GeneratedResource);
    }

    [Fact]
    public void ImplementsIKubernetesCustomResourceResource()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var resource = new KubernetesCustomResourceResource("my-resource", environment);

        Assert.IsAssignableFrom<IKubernetesCustomResourceResource>(resource);
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

    [Fact]
    public void Build_WithMultipleSpecifications()
    {
        var environment = new KubernetesEnvironmentResource("env");
        var spec = new SimpleCustomResourceSpec("test", 1, new NestedCustomResourceSpec("data"));
        var resource = new KubernetesCustomResourceResource("my-resource", environment)
        {
            ApiVersion = "custom.io/v1",
            Kind = "MyCustomResource",
            Spec = spec
        };

        var result = (CustomResourceV1)resource.Build();

        Assert.Equal("custom.io/v1", result.ApiVersion);
        Assert.Equal("MyCustomResource", result.Kind);
        Assert.Equal("my-resource", result.Metadata.Name);
        Assert.Same(spec, result.Spec);
    }
}
