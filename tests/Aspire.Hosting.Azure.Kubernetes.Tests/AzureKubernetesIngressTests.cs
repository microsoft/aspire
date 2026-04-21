// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

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

    [Fact]
    public void WithApplicationGateway_ExplicitSubnet_ProvisionesAgcBicepResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var aksSubnet = vnet.AddSubnet("aks-subnet", "10.0.0.0/22");
        var agcSubnet = vnet.AddSubnet("agc-subnet", "10.0.4.0/24");

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSubnet(aksSubnet)
            .WithApplicationGateway(agcSubnet);

        // Verify the AGC Bicep resource was added to the model
        var agcResource = builder.Resources
            .OfType<AzureBicepResource>()
            .FirstOrDefault(r => r.Name == "aks-agc");
        Assert.NotNull(agcResource);

        // Verify the Bicep template contains the traffic controller and association
        var bicep = agcResource.GetBicepTemplateString();
        Assert.Contains("Microsoft.ServiceNetworking/trafficControllers", bicep);
        Assert.Contains("Microsoft.ServiceNetworking/trafficControllers/frontends", bicep);
        Assert.Contains("Microsoft.ServiceNetworking/trafficControllers/associations", bicep);
        Assert.Contains("output agcId string", bicep);
        Assert.Contains("output agcFrontendFqdn string", bicep);
        Assert.Contains("param subnetId string", bicep);

        // Verify AGC resource ID is stored on the AKS resource
        Assert.NotNull(aks.Resource.AgcResourceId);
        Assert.NotNull(aks.Resource.AgcFrontendFqdn);
    }

    [Fact]
    public void WithApplicationGateway_AutoSubnet_CreatesAgcSubnet()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var vnet = builder.AddAzureVirtualNetwork("vnet", "10.0.0.0/16");
        var aksSubnet = vnet.AddSubnet("aks-subnet", "10.0.0.0/22");

        var aks = builder.AddAzureKubernetesEnvironment("aks")
            .WithSubnet(aksSubnet)
            .WithApplicationGateway();

        // Verify the AGC subnet was auto-created
        var agcSubnet = builder.Resources
            .OfType<AzureSubnetResource>()
            .FirstOrDefault(r => r.Name == "agc-subnet");
        Assert.NotNull(agcSubnet);

        // Verify the AGC Bicep resource was added
        var agcResource = builder.Resources
            .OfType<AzureBicepResource>()
            .FirstOrDefault(r => r.Name == "aks-agc");
        Assert.NotNull(agcResource);

        Assert.NotNull(aks.Resource.AgcResourceId);
    }

    [Fact]
    public void WithApplicationGateway_NoSubnet_ThrowsInvalidOperationException()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var aks = builder.AddAzureKubernetesEnvironment("aks");

#pragma warning disable IDE0200
        Assert.Throws<InvalidOperationException>(() => aks.WithApplicationGateway());
#pragma warning restore IDE0200
    }
}
