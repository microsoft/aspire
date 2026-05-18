// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // AzureSubnetResource evaluation-only.
#pragma warning disable ASPIRECOMPUTE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class SimplifiedDeploymentTests
{
    [Fact]
    public void WithSimplifiedDeployment_BareCall_RegistersExpectedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // VNet, both subnets, gateway, AGC LB, cert-manager and issuer should all exist.
        Assert.Single(model.Resources.OfType<KubernetesGatewayResource>(), g => g.Name == "public-gw");
        Assert.Single(model.Resources.OfType<AzureKubernetesLoadBalancerResource>(), lb => lb.Name == "public");
        Assert.Single(model.Resources.OfType<CertManagerResource>(), c => c.Name == "cert-manager");
        Assert.Single(model.Resources.OfType<CertManagerIssuerResource>(), i => i.Name == "letsencrypt");
    }

    [Fact]
    public void WithSimplifiedDeployment_OverridesAddressSpace_PropagatesToVnetAndSubnets()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.AddressSpace = "172.16.0.0/16";
            o.AksSubnetCidr = "172.16.0.0/22";
            o.LoadBalancerSubnetCidr = "172.16.4.0/24";
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var vnet = Assert.Single(model.Resources.OfType<AzureVirtualNetworkResource>());
        Assert.Equal("aks-vnet", vnet.Name);
        // Subnets are registered as model resources too.
        Assert.Contains(model.Resources.OfType<AzureSubnetResource>(), s => s.Name == "aks-nodes");
        Assert.Contains(model.Resources.OfType<AzureSubnetResource>(), s => s.Name == "alb-public");
    }

    [Fact]
    public async Task WithSimplifiedDeployment_AutoRoutesExternalHttpEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        builder.AddContainer("api", "myimage")
               .WithHttpEndpoint(targetPort: 8080, name: "http")
               .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();
        await app.RunAsync();

        // Single external frontend is promoted to "/" so the bare gateway URL works.
        var route = Assert.Single(gateway.Routes);
        Assert.Equal("/", route.Path);
        Assert.Equal("api", route.Endpoint.Resource.Name);
    }

    [Fact]
    public async Task WithSimplifiedDeployment_RespectsUserAuthoredRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        var api = builder.AddContainer("api", "myimage")
                         .WithHttpEndpoint(targetPort: 8080, name: "http")
                         .WithExternalHttpEndpoints();

        // User wires the gateway by hand to a different path — the auto-router must skip "api".
        var gatewayResource = builder.Resources.OfType<KubernetesGatewayResource>().Single();
        var gatewayBuilder = builder.CreateResourceBuilder(gatewayResource);
        gatewayBuilder.WithRoute("/custom-api", api.GetEndpoint("http"));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();
        await app.RunAsync();

        var apiRoutes = gateway.Routes.Where(r => r.Endpoint.Resource.Name == "api").ToList();
        Assert.Single(apiRoutes);
        Assert.Equal("/custom-api", apiRoutes[0].Path);
    }

    [Fact]
    public async Task WithSimplifiedDeployment_DisableAutoRoute_LeavesNoUserRoutes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.AutoRouteExternalEndpoints = false;
        });

        builder.AddContainer("api", "myimage")
               .WithHttpEndpoint(targetPort: 8080, name: "http")
               .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();
        await app.RunAsync();

        Assert.Empty(gateway.Routes);
    }

    [Fact]
    public void WithSimplifiedDeployment_DisableTls_DoesNotProvisionCertManager()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.EnableTls = false;
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(model.Resources.OfType<CertManagerResource>());
        Assert.Empty(model.Resources.OfType<CertManagerIssuerResource>());
        Assert.Single(model.Resources.OfType<KubernetesGatewayResource>());
    }

    [Fact]
    public void WithSimplifiedDeployment_ThrowsOnNullBuilder()
    {
        using var b = TestDistributedApplicationBuilder.Create();
        var acmeEmail = b.AddParameter("acme-email", "ops@contoso.com");

        IResourceBuilder<AzureKubernetesEnvironmentResource> nullBuilder = null!;

        Assert.Throws<ArgumentNullException>(() => nullBuilder.WithSimplifiedDeployment(acmeEmail));
    }

    [Fact]
    public void WithSimplifiedDeployment_ThrowsOnNullAcmeEmail()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Throws<ArgumentNullException>(() => aks.WithSimplifiedDeployment(null!));
    }

    [Fact]
    public async Task WithSimplifiedDeployment_Throws_WhenMultipleExternalResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        builder.AddContainer("api", "myimage")
               .WithHttpEndpoint(targetPort: 8080, name: "http")
               .WithExternalHttpEndpoints();
        builder.AddContainer("web", "myimage")
               .WithHttpEndpoint(targetPort: 8081, name: "http")
               .WithExternalHttpEndpoints();

        using var app = builder.Build();

        // The auto-router enforces the single-frontend contract during the
        // prepare-deployment-targets pipeline step (which runs under app.RunAsync in
        // Publish mode), so the throw surfaces here rather than at builder construction
        // time. Applications that need multiple external frontends must drop down to
        // the verbose AddAzureKubernetesEnvironment + gateway/route/cert wiring path.
        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.RunAsync());
        Assert.Contains("WithSimplifiedDeployment", ex.Message, StringComparison.Ordinal);
        Assert.Contains("api", ex.Message, StringComparison.Ordinal);
        Assert.Contains("web", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddAzureKubernetesEnvironment", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithSimplifiedDeployment_MultiEndpointSingleResource_CollapsesToOneRoute()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        // A project with WithExternalHttpEndpoints annotates BOTH http and https
        // endpoints, but they front the same backend Kestrel — emit one route, not two.
        builder.AddContainer("api", "myimage")
               .WithHttpEndpoint(targetPort: 8080, name: "http")
               .WithHttpsEndpoint(targetPort: 8443, name: "https")
               .WithExternalHttpEndpoints();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();
        await app.RunAsync();

        var route = Assert.Single(gateway.Routes);
        Assert.Equal("/", route.Path);
        // Prefer the plaintext endpoint because TLS terminates at the gateway and the
        // in-cluster Service typically listens http.
        Assert.Equal("http", route.Endpoint.EndpointName);
    }

    [Fact]
    public void WithSimplifiedDeployment_AddsSystemAndUserNodePoolsByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        // Default options should provision a workload pool alongside the system pool so
        // workloads don't have to share the system pool with cert-manager / kube-system.
        var systemPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
        var userPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.User);
        Assert.Equal("workload", userPool.Name);
        Assert.Equal("Standard_D2as_v5", systemPool.VmSize);
        Assert.Equal("Standard_D2as_v5", userPool.VmSize);
    }

    [Fact]
    public void WithSimplifiedDeployment_IncludeUserNodePoolFalse_OmitsUserPool()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.IncludeUserNodePool = false;
        });

        Assert.DoesNotContain(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.User);
        Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
    }

    [Fact]
    public void WithSimplifiedDeployment_VmSizeParameter_OverridesStringDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        // Parameter values resolve from configuration at AppHost startup, so a hardcoded
        // default here stands in for a `aspire deploy -p systemVmSize=...` override.
        var systemSku = builder.AddParameter("systemVmSize", "Standard_E2s_v5");
        var userSku = builder.AddParameter("userVmSize", "Standard_E4s_v5");

        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.SystemNodePoolVmSizeParameter = systemSku;
            o.UserNodePoolVmSizeParameter = userSku;
        });

        var systemPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
        var userPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.User);
        Assert.Equal("Standard_E2s_v5", systemPool.VmSize);
        Assert.Equal("Standard_E4s_v5", userPool.VmSize);
    }

    [Fact]
    public void WithSimplifiedDeployment_NodePoolCounts_AppliedToConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.SystemNodePoolMinCount = 2;
            o.SystemNodePoolMaxCount = 5;
            o.UserNodePoolMinCount = 3;
            o.UserNodePoolMaxCount = 10;
            o.UserNodePoolName = "apps";
        });

        var systemPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
        var userPool = Assert.Single(aks.Resource.NodePools, p => p.Name == "apps");
        Assert.Equal(2, systemPool.MinCount);
        Assert.Equal(5, systemPool.MaxCount);
        Assert.Equal(3, userPool.MinCount);
        Assert.Equal(10, userPool.MaxCount);
    }

    [Fact]
    public void WithSimplifiedDeployment_NoHostname_GatewayHasNoHostnames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();

        // No explicit hostname means the gateway listener stays unbound, and the
        // tls-fqdn-discovery pipeline step is the one that will patch in the
        // ALB-assigned *.alb.azure.com hostname post-deploy.
        Assert.Empty(gateway.Hostnames);
    }

    [Fact]
    public async Task WithSimplifiedDeployment_HostnameString_FlowsToGateway()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.Hostname = "app.contoso.com";
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();

        var hostname = Assert.Single(gateway.Hostnames);
        var resolved = await hostname.GetValueAsync(default);
        Assert.Equal("app.contoso.com", resolved);
    }

    [Fact]
    public async Task WithSimplifiedDeployment_HostnameParameter_FlowsToGateway()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var hostnameParam = builder.AddParameter("hostname", "app.contoso.com");
        builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.HostnameParameter = hostnameParam;
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var gateway = model.Resources.OfType<KubernetesGatewayResource>().Single();

        var hostname = Assert.Single(gateway.Hostnames);
        var resolved = await hostname.GetValueAsync(default);
        Assert.Equal("app.contoso.com", resolved);
    }

    [Fact]
    public void WithSimplifiedDeployment_HostnameAndHostnameParameter_BothSet_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var hostnameParam = builder.AddParameter("hostname", "param.contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        // Both set => ambiguous intent. Fail eagerly with a message that names both
        // properties so the user can see which pair conflicts without diffing options.
        var ex = Assert.Throws<ArgumentException>(() => aks.WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.Hostname = "string.contoso.com";
            o.HostnameParameter = hostnameParam;
        }));
        Assert.Contains(nameof(SimplifiedDeploymentOptions.Hostname), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SimplifiedDeploymentOptions.HostnameParameter), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSimplifiedDeployment_SystemNodePoolVmSizeAndParameter_BothSet_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var systemSku = builder.AddParameter("systemVmSize", "Standard_E2s_v5");
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var ex = Assert.Throws<ArgumentException>(() => aks.WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.SystemNodePoolVmSize = "Standard_D4as_v5";
            o.SystemNodePoolVmSizeParameter = systemSku;
        }));
        Assert.Contains(nameof(SimplifiedDeploymentOptions.SystemNodePoolVmSize), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SimplifiedDeploymentOptions.SystemNodePoolVmSizeParameter), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSimplifiedDeployment_UserNodePoolVmSizeAndParameter_BothSet_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var userSku = builder.AddParameter("userVmSize", "Standard_E4s_v5");
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var ex = Assert.Throws<ArgumentException>(() => aks.WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.UserNodePoolVmSize = "Standard_D4as_v5";
            o.UserNodePoolVmSizeParameter = userSku;
        }));
        Assert.Contains(nameof(SimplifiedDeploymentOptions.UserNodePoolVmSize), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(SimplifiedDeploymentOptions.UserNodePoolVmSizeParameter), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSimplifiedDeployment_VmSizeString_AppliedWhenParameterNotSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail, o =>
        {
            o.SystemNodePoolVmSize = "Standard_D4as_v5";
            o.UserNodePoolVmSize = "Standard_E4s_v5";
        });

        var systemPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
        var userPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.User);
        Assert.Equal("Standard_D4as_v5", systemPool.VmSize);
        Assert.Equal("Standard_E4s_v5", userPool.VmSize);
    }

    [Fact]
    public void WithSimplifiedDeployment_VmSizeDefaults_AppliedWhenNeitherSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        var acmeEmail = builder.AddParameter("acme-email", "ops@contoso.com");
        var aks = builder.AddAzureKubernetesEnvironment("aks").WithSimplifiedDeployment(acmeEmail);

        // No string, no parameter => both pools fall back to the same shared default
        // (Standard_D2as_v5) — the smallest size that fits cert-manager + ALB controller
        // + kube-system on the system pool without scheduling pressure.
        var systemPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.System);
        var userPool = Assert.Single(aks.Resource.NodePools, p => p.Mode == AksNodePoolMode.User);
        Assert.Equal("Standard_D2as_v5", systemPool.VmSize);
        Assert.Equal("Standard_D2as_v5", userPool.VmSize);
    }
}
