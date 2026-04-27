// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesIngressTests
{
    [Fact]
    public async Task EnvironmentIngressAnnotation_GeneratesIngressForExternalHttpEndpoint()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddKubernetesEnvironment("env")
            .WithIngress(ctx =>
            {
                foreach (var endpoint in ctx.ExternalHttpEndpoints)
                {
                    var ingress = new Ingress
                    {
                        Metadata = { Name = $"{ctx.Resource.Name}-ingress" },
                        Spec =
                        {
                            IngressClassName = "test-ingress-class",
                            Rules =
                            {
                                new IngressRuleV1
                                {
                                    Http = new HttpIngressRuleValueV1
                                    {
                                        Paths =
                                        {
                                            new HttpIngressPathV1
                                            {
                                                Path = "/",
                                                PathType = "Prefix",
                                                Backend = new IngressBackendV1
                                                {
                                                    Service = new IngressServiceBackendV1
                                                    {
                                                        Name = ctx.KubernetesResource.Service!.Metadata.Name,
                                                        Port = new ServiceBackendPortV1 { Number = 8080 }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    };
                    ctx.KubernetesResource.AdditionalResources.Add(ingress);
                }
                return Task.CompletedTask;
            });

        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // Verify ingress YAML was generated
        var ingressPath = Path.Combine(tempDir.Path, "templates/myapp/ingress.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress.yaml at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("Ingress", content);
        Assert.Contains("test-ingress-class", content);
    }

    [Fact]
    public async Task NoExternalEndpoints_NoIngressGenerated()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var ingressCallbackInvoked = false;

        builder.AddKubernetesEnvironment("env")
            .WithIngress(ctx =>
            {
                ingressCallbackInvoked = true;
                return Task.CompletedTask;
            });

        // No external endpoints — default is IsExternal = false
        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        Assert.False(ingressCallbackInvoked, "Ingress callback should not be invoked when no external endpoints exist");
    }

    [Fact]
    public async Task ResourceAnnotation_OverridesEnvironmentAnnotation()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var environmentCallbackInvoked = false;
        var resourceCallbackInvoked = false;

        builder.AddKubernetesEnvironment("env")
            .WithIngress(ctx =>
            {
                environmentCallbackInvoked = true;
                return Task.CompletedTask;
            });

        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true)
            .WithKubernetesIngress(ctx =>
            {
                resourceCallbackInvoked = true;
                // Add a resource-specific ingress
                var ingress = new Ingress
                {
                    Metadata = { Name = $"{ctx.Resource.Name}-custom-ingress" },
                    Spec = { IngressClassName = "custom-class" }
                };
                ctx.KubernetesResource.AdditionalResources.Add(ingress);
                return Task.CompletedTask;
            });

        var app = builder.Build();
        app.Run();

        Assert.False(environmentCallbackInvoked, "Environment callback should not be invoked when resource has its own annotation");
        Assert.True(resourceCallbackInvoked, "Resource-level callback should be invoked");

        var ingressPath = Path.Combine(tempDir.Path, "templates/myapp/custom-ingress.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected custom-ingress.yaml at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("custom-class", content);
    }

    [Fact]
    public async Task WithIngressFalse_DisablesIngress()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var callbackInvoked = false;

        builder.AddKubernetesEnvironment("env")
            .WithIngress(ctx =>
            {
                callbackInvoked = true;
                return Task.CompletedTask;
            })
            .WithIngress(false);

        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        Assert.False(callbackInvoked, "Ingress callback should not be invoked when WithIngress(false) is called");
    }

    [Fact]
    public async Task NoAnnotation_NoIngressGenerated_BackwardCompatible()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        // No WithIngress call — backward compatible
        builder.AddKubernetesEnvironment("env");

        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // Should not have an ingress.yaml
        var ingressPath = Path.Combine(tempDir.Path, "templates/myapp/ingress.yaml");
        Assert.False(File.Exists(ingressPath), "No ingress should be generated when no annotation is set");
    }

    [Fact]
    public async Task IngressContext_ContainsCorrectExternalEndpoints()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        IReadOnlyList<EndpointAnnotation>? capturedEndpoints = null;

        builder.AddKubernetesEnvironment("env")
            .WithIngress(ctx =>
            {
                capturedEndpoints = ctx.ExternalHttpEndpoints;
                return Task.CompletedTask;
            });

        builder.AddContainer("myapp", "nginx")
            .WithHttpEndpoint(name: "web", targetPort: 8080)
            .WithEndpoint("web", e => e.IsExternal = true)
            .WithEndpoint(name: "internal", targetPort: 9090, scheme: "http")
            .WithEndpoint(name: "grpc", targetPort: 50051, scheme: "http");

        var app = builder.Build();
        app.Run();

        Assert.NotNull(capturedEndpoints);
        Assert.Single(capturedEndpoints); // Only "web" is external
        Assert.Equal("web", capturedEndpoints[0].Name);
    }
}
