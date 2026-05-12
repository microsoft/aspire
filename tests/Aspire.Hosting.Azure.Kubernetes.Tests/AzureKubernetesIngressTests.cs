// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesIngressTests
{
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
        var ingressPath = Path.Combine(tempDir.Path, "templates", "public", "public.yaml");
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

    [Fact]
    public async Task WithLoadBalancer_OnGateway_AnnotatesAndDefaultsClass()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb1", albSubnet);

        var gateway = aks.AddGateway("public").WithLoadBalancer(lb);

        Assert.NotNull(gateway.Resource.GatewayClassName);
        var resolvedClass = await gateway.Resource.GatewayClassName!.GetValueAsync(default);
        Assert.Equal("azure-alb-external", resolvedClass);

        Assert.True(gateway.Resource.GatewayAnnotations.TryGetValue("alb.networking.azure.io/alb-name", out var albNameRef));
        Assert.Equal("alb-lb1", await albNameRef!.GetValueAsync(default));

        Assert.True(gateway.Resource.GatewayAnnotations.TryGetValue("alb.networking.azure.io/alb-namespace", out var albNsRef));
        Assert.Equal("default", await albNsRef!.GetValueAsync(default));
    }

    [Fact]
    public async Task WithLoadBalancer_OnIngress_AnnotatesAndDefaultsClass()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb1", albSubnet);

        var ingress = aks.AddIngress("public").WithLoadBalancer(lb);

        Assert.NotNull(ingress.Resource.IngressClassName);
        var resolvedClass = await ingress.Resource.IngressClassName!.GetValueAsync(default);
        Assert.Equal("azure-alb-external", resolvedClass);

        Assert.True(ingress.Resource.IngressAnnotations.TryGetValue("alb.networking.azure.io/alb-name", out var albNameRef));
        Assert.Equal("alb-lb1", await albNameRef!.GetValueAsync(default));

        Assert.True(ingress.Resource.IngressAnnotations.TryGetValue("alb.networking.azure.io/alb-namespace", out var albNsRef));
        Assert.Equal("default", await albNsRef!.GetValueAsync(default));
    }

    [Fact]
    public async Task WithLoadBalancer_RespectsExplicitGatewayClass()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var albSubnet = vnet.AddSubnet("alb", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var lb = aks.AddLoadBalancer("lb1", albSubnet);

        // Explicit class set BEFORE WithLoadBalancer is preserved; AGC annotations
        // are still applied so AGC can still discover the LB.
        var gateway = aks.AddGateway("public")
            .WithGatewayClass("custom-class")
            .WithLoadBalancer(lb);

        Assert.NotNull(gateway.Resource.GatewayClassName);
        var resolvedClass = await gateway.Resource.GatewayClassName!.GetValueAsync(default);
        Assert.Equal("custom-class", resolvedClass);

        Assert.True(gateway.Resource.GatewayAnnotations.ContainsKey("alb.networking.azure.io/alb-name"));
        Assert.True(gateway.Resource.GatewayAnnotations.ContainsKey("alb.networking.azure.io/alb-namespace"));
    }
}
