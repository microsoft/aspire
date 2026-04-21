// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Kubernetes.Resources;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesIngressTests
{
    // With AKS, there are two compute environments (AKS + inner K8s),
    // so the Helm chart output goes to {outputDir}/{k8sEnvName}/...
    private const string K8sEnvSubdir = "aks-k8s";

    [Fact]
    public async Task AksEnvironment_GeneratesIngressForExternalHttpEndpoint()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddAzureKubernetesEnvironment("aks");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // Verify ingress YAML was generated
        var templatesDir = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "myapi");
        Assert.True(Directory.Exists(templatesDir), $"Templates directory not found at {templatesDir}");

        var ingressFiles = Directory.GetFiles(templatesDir, "*ingress*");
        Assert.Single(ingressFiles);

        var content = await File.ReadAllTextAsync(ingressFiles[0]);

        // Verify it has the AGC ingress class
        Assert.Contains("azure-alb-external", content);
        // Verify it's an Ingress resource
        Assert.Contains("Ingress", content);
    }

    [Fact]
    public async Task AksEnvironment_NoIngressForInternalEndpoints()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddAzureKubernetesEnvironment("aks");

        // No external endpoints
        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        var app = builder.Build();
        app.Run();

        // No ingress file should exist for internal-only endpoints
        var templatesDir = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "myapi");
        if (Directory.Exists(templatesDir))
        {
            var ingressFiles = Directory.GetFiles(templatesDir, "*ingress*");
            Assert.Empty(ingressFiles);
        }
    }

    [Fact]
    public async Task AksEnvironment_WithIngressFalse_DisablesIngress()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // Disable ingress on the inner K8s environment
        var k8sEnv = aks.Resource.KubernetesEnvironment;
        var k8sEnvBuilder = builder.CreateResourceBuilder(k8sEnv);
        k8sEnvBuilder.WithIngress(false);

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // No ingress file should exist when ingress is disabled
        var templatesDir = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "myapi");
        if (Directory.Exists(templatesDir))
        {
            var ingressFiles = Directory.GetFiles(templatesDir, "*ingress*");
            Assert.Empty(ingressFiles);
        }
    }

    [Fact]
    public async Task AksEnvironment_ThirdPartyOverride_ReplacesDefault()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // Override with a custom ingress class (simulates third-party like Ngrok)
        var k8sEnv = aks.Resource.KubernetesEnvironment;
        var k8sEnvBuilder = builder.CreateResourceBuilder(k8sEnv);
        k8sEnvBuilder.WithIngress(ctx =>
        {
            foreach (var endpoint in ctx.ExternalHttpEndpoints)
            {
                var ingress = new Ingress
                {
                    Metadata = { Name = $"{ctx.Resource.Name}-{endpoint.Name}-ngrok-ingress" },
                    Spec = { IngressClassName = "ngrok" }
                };
                ctx.KubernetesResource.AdditionalResources.Add(ingress);
            }
            return Task.CompletedTask;
        });

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEndpoint("http", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // Should use the third-party ingress class, not AGC
        var templatesDir = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "myapi");
        Assert.True(Directory.Exists(templatesDir), $"Templates directory not found at {templatesDir}");

        var ingressFiles = Directory.GetFiles(templatesDir, "*ingress*");
        Assert.Single(ingressFiles);

        var content = await File.ReadAllTextAsync(ingressFiles[0]);
        Assert.Contains("ngrok", content);
        Assert.DoesNotContain("azure-alb-external", content);
    }

    [Fact]
    public async Task AksEnvironment_MultipleExternalEndpoints_GeneratesMultipleIngresses()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        builder.AddAzureKubernetesEnvironment("aks");

        builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(name: "web", targetPort: 8080)
            .WithEndpoint("web", e => e.IsExternal = true)
            .WithHttpEndpoint(name: "admin", targetPort: 9090)
            .WithEndpoint("admin", e => e.IsExternal = true);

        var app = builder.Build();
        app.Run();

        // Should have two ingress files
        var templatesDir = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "myapi");
        Assert.True(Directory.Exists(templatesDir), $"Templates directory not found at {templatesDir}");

        var ingressFiles = Directory.GetFiles(templatesDir, "*ingress*");
        Assert.Equal(2, ingressFiles.Length);
    }
}
