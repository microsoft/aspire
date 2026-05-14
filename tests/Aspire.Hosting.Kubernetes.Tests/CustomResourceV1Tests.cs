// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Kubernetes.Resources;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Tests;

public class CustomResourceV1Tests
{
    [Fact]
    public void Constructor_WithApiVersionAndKind_SetsProperties()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");

        Assert.Equal("v1", resource.ApiVersion);
        Assert.Equal("ConfigMap", resource.Kind);
    }

    [Fact]
    public void Constructor_WithEmptyApiVersion()
    {
        var resource = new CustomResourceV1("", "Pod");

        Assert.Equal("", resource.ApiVersion);
        Assert.Equal("Pod", resource.Kind);
    }

    [Fact]
    public void Constructor_WithEmptyKind()
    {
        var resource = new CustomResourceV1("v1", "");

        Assert.Equal("v1", resource.ApiVersion);
        Assert.Equal("", resource.Kind);
    }

    [Fact]
    public void Metadata_DefaultIsInitialized()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");

        Assert.NotNull(resource.Metadata);
        Assert.IsType<ObjectMetaV1>(resource.Metadata);
    }

    [Fact]
    public void Metadata_CanBeModified()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");
        resource.Metadata.Name = "my-config";
        resource.Metadata.Namespace = "default";

        Assert.Equal("my-config", resource.Metadata.Name);
        Assert.Equal("default", resource.Metadata.Namespace);
    }

    [Fact]
    public void Spec_DefaultIsEmptyObject()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");

        Assert.NotNull(resource.Spec);
        Assert.IsType<object>(resource.Spec);
    }

    [Fact]
    public void Spec_CanBeSetToCustomObject()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");
        var spec = new { data = new { key = "value" } };

        resource.Spec = spec;

        Assert.Same(spec, resource.Spec);
    }

    [Fact]
    public void Spec_CanBeSetToPrimitiveType()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");
        resource.Spec = "string value";

        Assert.Equal("string value", resource.Spec);
    }

    [Fact]
    public void Spec_CanBeSetToCollectionType()
    {
        var resource = new CustomResourceV1("v1", "List");
        var spec = new[] { 1, 2, 3 };

        resource.Spec = spec;

        Assert.Same(spec, resource.Spec);
    }

    [Fact]
    public void InheritsFromBaseKubernetesResource()
    {
        var resource = new CustomResourceV1("apps/v1", "Deployment");

        Assert.IsAssignableFrom<BaseKubernetesResource>(resource);
    }

    [Fact]
    public void HasYamlSerializableAttribute()
    {
        var type = typeof(CustomResourceV1);
        var attribute = type.GetCustomAttributes(typeof(YamlSerializableAttribute), inherit: false).FirstOrDefault();

        Assert.NotNull(attribute);
    }

    [Fact]
    public void Serializable()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap")
        {
            Metadata = new ObjectMetaV1 { Name = "my-config" },
            Spec = new { data = new { key = "value" } }
        };

        var serializer = new SerializerBuilder().Build();
        var yaml = serializer.Serialize(resource);

        Assert.Contains("apiVersion: v1", yaml);
        Assert.Contains("kind: ConfigMap", yaml);
        Assert.Contains("metadata:", yaml);
        Assert.Contains("name: my-config", yaml);
        Assert.Contains("spec:", yaml);
    }

    [Fact]
    public void MultipleConstructorCalls_CreatesDistinctInstances()
    {
        var resource1 = new CustomResourceV1("v1", "ConfigMap");
        var resource2 = new CustomResourceV1("v1", "ConfigMap");

        Assert.NotSame(resource1, resource2);
    }

    [Fact]
    public void WithComplexSpec_SerializesCorrectly()
    {
        var spec = new
        {
            replicas = 3,
            template = new
            {
                metadata = new { labels = new { app = "nginx" } },
                spec = new
                {
                    containers = new[] { new { name = "nginx", image = "nginx:latest" } }
                }
            }
        };

        var resource = new CustomResourceV1("apps/v1", "Deployment")
        {
            Metadata = new ObjectMetaV1 { Name = "nginx-deployment" },
            Spec = spec
        };

        var serializer = new SerializerBuilder().Build();
        var yaml = serializer.Serialize(resource);

        Assert.Contains("apiVersion: apps/v1", yaml);
        Assert.Contains("kind: Deployment", yaml);
        Assert.Contains("name: nginx-deployment", yaml);
        Assert.Contains("replicas: 3", yaml);
    }

    [Fact]
    public void ApiVersion_And_Kind_CanBeDifferentForCustomResources()
    {
        var resource1 = new CustomResourceV1("custom.io/v1", "MyCustomResource");
        var resource2 = new CustomResourceV1("cert-manager.io/v1", "Certificate");

        Assert.Equal("custom.io/v1", resource1.ApiVersion);
        Assert.Equal("MyCustomResource", resource1.Kind);
        Assert.Equal("cert-manager.io/v1", resource2.ApiVersion);
        Assert.Equal("Certificate", resource2.Kind);
    }

    [Fact]
    public void Spec_WithNullValue()
    {
        var resource = new CustomResourceV1("v1", "ConfigMap");
        resource.Spec = null!;

        Assert.Null(resource.Spec);
    }
}
