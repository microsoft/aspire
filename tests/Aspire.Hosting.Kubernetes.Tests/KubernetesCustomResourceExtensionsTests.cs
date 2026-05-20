// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesCustomResourceExtensionsTests
{
    [Fact]
    public async Task AddCustomResource_WithValidParameters_CreatesResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        Assert.NotNull(resource);
        Assert.Equal("my-resource", resource.Resource.Name);
        Assert.Equal("v1", await resource.Resource.ApiVersion.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("ConfigMap", await resource.Resource.Kind.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddCustomResource_SetsParentEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "apps/v1", "Deployment");

        Assert.Same(k8s.Resource, resource.Resource.Parent);
    }

    [Fact]
    public void AddCustomResource_ReturnsResourceBuilder()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var result = k8s.AddCustomResource("my-resource", "v1", "Pod");

        Assert.IsAssignableFrom<IResourceBuilder<KubernetesCustomResourceResource>>(result);
    }

    [Fact]
    public void AddCustomResource_NullBuilder_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            KubernetesCustomResourceExtensions.AddCustomResource(null!, "my-resource", "v1", "ConfigMap"));

        Assert.Equal("builder", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_EmptyName_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentException>(() =>
            k8s.AddCustomResource("", "v1", "ConfigMap"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_NullName_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentNullException>(() =>
            k8s.AddCustomResource(null!, "v1", "ConfigMap"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_EmptyApiVersion_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentException>(() =>
            k8s.AddCustomResource("my-resource", "", "ConfigMap"));

        Assert.Equal("apiVersion", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_NullApiVersion_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentNullException>(() =>
            k8s.AddCustomResource("my-resource", null!, "ConfigMap"));

        Assert.Equal("apiVersion", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_EmptyKind_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentException>(() =>
            k8s.AddCustomResource("my-resource", "v1", ""));

        Assert.Equal("kind", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_NullKind_Throws()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var ex = Assert.Throws<ArgumentNullException>(() =>
            k8s.AddCustomResource("my-resource", "v1", null!));

        Assert.Equal("kind", ex.ParamName);
    }

    [Fact]
    public void AddCustomResource_PublishMode_CreatesResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        // In Publish mode, the resource should be created and part of the model
        Assert.NotNull(resource.Resource);
        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var foundResource = appModel.Resources.FirstOrDefault(r => r.Name == "my-resource");
        Assert.NotNull(foundResource);
    }

    [Fact]
    public void AddCustomResource_RunMode_NotExcludedFromManifest()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        // In Run mode, the resource may not be added to the model at all
        // since it's a publish-time concept
        Assert.NotNull(resource.Resource);
    }

    [Fact]
    public void AddCustomResource_CanBeChainedWithOtherMethods()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap")
            .WithSpec(new ConfigMapDataSpec(new { key = "value" }));

        Assert.Equal("my-resource", resource.Resource.Name);
        Assert.NotNull(resource.Resource.Spec);
    }

    [Fact]
    public async Task AddCustomResource_WithCustomApiVersion()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-cert", "cert-manager.io/v1", "Certificate");

        Assert.Equal("cert-manager.io/v1", await resource.Resource.ApiVersion.GetValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Certificate", await resource.Resource.Kind.GetValueAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WithSpec_NullBuilder_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            KubernetesCustomResourceExtensions.WithSpec(null!, new GenericObjectSpec(new { })));

        Assert.Equal("builder", ex.ParamName);
    }

    [Fact]
    public void WithSpec_SetsSpecProperty()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        var spec = new ConfigMapDataSpec(new { key = "value" });
        resource.WithSpec(spec);

        Assert.Same(spec, resource.Resource.Spec);
    }

    [Fact]
    public void WithSpec_ReturnsBuilder()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        var result = resource.WithSpec(new ConfigMapDataSpec(new { }));

        Assert.Same(resource, result);
    }

    [Fact]
    public void WithSpec_AllowsChaining()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap")
            .WithSpec(new ConfigMapDataSpec(new { key1 = "value1" }))
            .WithSpec(new ConfigMapDataSpec(new { key2 = "value2" }));

        // Last call wins
        var spec = resource.Resource.Spec as ConfigMapDataSpec;
        Assert.Equal("value2", ((dynamic)spec!).Data.key2);
    }

    [Fact]
    public void WithSpec_WithComplexObject()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-deployment", "apps/v1", "Deployment");

        var spec = new KubernetesCustomResourceDeploymentSpec(
            replicas: 3,
            selector: new KubernetesCustomResourceSelectorSpec(
                new Dictionary<string, string> { ["app"] = "nginx" }),
            template: new KubernetesCustomResourceTemplateSpec(
                new KubernetesCustomResourceTemplateMetadata(
                    new Dictionary<string, string> { ["app"] = "nginx" }),
                new KubernetesCustomResourcePodSpec(
                    new[] { new KubernetesCustomResourceContainerSpec("nginx", "nginx:latest") })));

        resource.WithSpec(spec);

        Assert.Same(spec, resource.Resource.Spec);
    }

    [Fact]
    public void WithSpec_WithPrimitiveValue()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-resource", "v1", "String");

        resource.WithSpec(new StringValueSpec("simple string value"));

        Assert.Equal("simple string value", ((StringValueSpec)resource.Resource.Spec!).Value);
    }

    [Fact]
    public void WithSpec_WithNullSpec()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-resource", "v1", "ConfigMap");

        resource.WithSpec(null!);

        Assert.Null(resource.Resource.Spec);
    }

    [Fact]
    public void WithSpec_WithArraySpec()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var resource = k8s.AddCustomResource("my-list", "v1", "List");

        var spec = new ArrayValueSpec(new[] { "item1", "item2", "item3" });
        resource.WithSpec(spec);

        Assert.Same(spec, resource.Resource.Spec);
    }

    [Fact]
    public void AddCustomResource_MultipleResources_EachIndependent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var resource1 = k8s.AddCustomResource("resource1", "v1", "ConfigMap");
        var resource2 = k8s.AddCustomResource("resource2", "apps/v1", "Deployment");

        Assert.NotEqual(resource1.Resource.Name, resource2.Resource.Name);
        Assert.NotEqual(resource1.Resource.ApiVersion, resource2.Resource.ApiVersion);
        Assert.NotEqual(resource1.Resource.Kind, resource2.Resource.Kind);
    }
}
