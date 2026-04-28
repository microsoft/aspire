// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesIngressTests
{
    // With AKS, there are two compute environments (AKS + inner K8s),
    // so the Helm chart output goes to {outputDir}/{k8sEnvName}/...
    private const string K8sEnvSubdir = "aks-k8s";

    [Fact]
    public async Task AksAddIngress_WithRoute_GeneratesIngressInHelmOutput()
    {
        using var tempDir = new TestTempDirectory();
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var ingress = aks.AddIngress("public")
            .WithIngressClass("nginx");

        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080);

        ingress.WithRoute("/", api.GetEndpoint("http"));

        var app = builder.Build();
        app.Run();

        // With AKS, the Helm output goes to the inner K8S env subdirectory
        var ingressPath = Path.Combine(tempDir.Path, K8sEnvSubdir, "templates", "public", "public.yaml");
        Assert.True(File.Exists(ingressPath), $"Expected ingress YAML at {ingressPath}");

        var content = await File.ReadAllTextAsync(ingressPath);
        Assert.Contains("Ingress", content);
        Assert.Contains("nginx", content);
    }

    [Fact]
    public void AksAddIngress_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var ingress = aks.AddIngress("public");

        // The ingress should be a child of the inner K8S environment, not the AKS environment
        Assert.IsType<KubernetesIngressResource>(ingress.Resource);
        Assert.IsType<KubernetesEnvironmentResource>(ingress.Resource.Parent);
    }

    [Fact]
    public void AksAddGateway_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var gateway = aks.AddGateway("public");

        Assert.IsType<KubernetesGatewayResource>(gateway.Resource);
        Assert.IsType<KubernetesEnvironmentResource>(gateway.Resource.Parent);
    }
}
