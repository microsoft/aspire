// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Kubernetes.Tests;

public class CertManagerTests
{
    [Fact]
    public void AddCertManager_RegistersWrapperAndHelmChartUnderSeparateNames()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var certManager = k8s.AddCertManager("cert-manager");

        Assert.Equal("cert-manager", certManager.Resource.Name);
        Assert.Same(k8s.Resource, certManager.Resource.Parent);

        // The wrapper keeps the natural "{name}" identifier; the helm chart is registered
        // under "{name}-chart" so both can coexist in the model without colliding.
        Assert.Equal("cert-manager-chart", certManager.Resource.HelmChart.Name);
        Assert.Equal("oci://quay.io/jetstack/charts/cert-manager", certManager.Resource.HelmChart.ChartReference);
        Assert.StartsWith("v", certManager.Resource.HelmChart.ChartVersion);

        // CRDs and Gateway API support are required for cert-manager to issue certificates
        // for Aspire-modeled Gateway resources, so they must be on by default.
        Assert.Equal("true", certManager.Resource.HelmChart.Values["crds.enabled"]);
        Assert.Equal("true", certManager.Resource.HelmChart.Values["config.enableGatewayAPI"]);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.Contains(appModel.Resources, r => r is CertManagerResource c && c.Name == "cert-manager");
        Assert.Contains(appModel.Resources, r => r is KubernetesHelmChartResource c && c.Name == "cert-manager-chart");
    }

    [Fact]
    public void AddIssuer_WithLetsEncryptProductionAndHttp01_PopulatesSpecAndSolver()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var certManager = k8s.AddCertManager("cert-manager");

        var issuer = certManager.AddIssuer("letsencrypt-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        Assert.Equal("letsencrypt-prod", issuer.Resource.Name);
        Assert.Same(certManager.Resource, issuer.Resource.Parent);
        Assert.Contains(issuer.Resource, certManager.Resource.Issuers);

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme-v02.api.letsencrypt.org/directory", spec.ServerUrl.Format);
        Assert.Equal("ops@contoso.com", spec.Email.Format);

        var solver = Assert.Single(issuer.Resource.Solvers);
        Assert.IsType<CertManagerHttp01SolverConfig>(solver);
    }

    [Fact]
    public void WithLetsEncryptStaging_UsesStagingDirectoryUrl()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");

        var issuer = certManager.AddIssuer("le-staging")
            .WithLetsEncryptStaging("ops@contoso.com");

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", spec.ServerUrl.Format);
    }

    [Fact]
    public void WithAcmeServer_AllowsCustomDirectoryUrl()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");

        var issuer = certManager.AddIssuer("custom-acme")
            .WithAcmeServer("https://acme.example.com/directory", "ops@contoso.com");

        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Equal("https://acme.example.com/directory", spec.ServerUrl.Format);
    }

    [Fact]
    public void Gateway_WithTls_Issuer_AddsClusterIssuerAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager("cert-manager")
            .AddIssuer("letsencrypt-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        var gateway = k8s.AddGateway("public")
            .WithGatewayClass("nginx")
            .WithTls(issuer);

        // The typed WithTls(issuer) overload should be equivalent to WithTls() + setting
        // the cert-manager.io/cluster-issuer annotation to the issuer's name.
        Assert.Single(gateway.Resource.TlsConfigs);
        Assert.True(gateway.Resource.GatewayAnnotations.TryGetValue(
            CertManagerExtensions.ClusterIssuerAnnotationKey, out var value));
        Assert.Equal("letsencrypt-prod", value!.Format);
    }

    [Fact]
    public async Task BuildClusterIssuerManifest_EmitsExpectedYamlForLetsEncryptHttp01()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager("cert-manager")
            .AddIssuer("LetsEncrypt-Prod") // intentionally mixed-case to verify DNS-1123 normalization
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        // A gateway that adopts this issuer must end up referenced as a parentRef on the
        // generated solver, so cert-manager's HTTP-01 HTTPRoute can attach to it.
        k8s.AddGateway("PUBLIC-GW")
            .WithGatewayClass("nginx")
            .WithTls(issuer);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var certManagerResource = model.Resources.OfType<CertManagerResource>().Single();
        var issuerResource = certManagerResource.Issuers.Single();

        var yaml = await CertManagerExtensions.BuildClusterIssuerManifestAsync(
            model,
            certManagerResource,
            issuerResource,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        // Object names go through ToKubernetesResourceName() (lowercasing) so they're valid
        // DNS-1123 subdomains regardless of the casing the user picked for the Aspire name.
        Assert.Contains("apiVersion: cert-manager.io/v1", yaml);
        Assert.Contains("kind: ClusterIssuer", yaml);
        Assert.Contains("name: letsencrypt-prod", yaml);
        Assert.Contains("server: https://acme-v02.api.letsencrypt.org/directory", yaml);
        Assert.Contains("email: ops@contoso.com", yaml);
        Assert.Contains("name: letsencrypt-prod-account-key", yaml);
        Assert.Contains("- http01:", yaml);
        Assert.Contains("gatewayHTTPRoute:", yaml);
        // Gateway parentRef must also be lowercase to match the actual emitted Gateway name.
        Assert.Contains("name: public-gw", yaml);
    }

    [Fact]
    public void Gateway_WithTls_Issuer_FromDifferentEnvironment_Throws()
    {
        // cert-manager is per-cluster, so an issuer can only TLS-protect gateways inside the
        // same Kubernetes environment. Cross-env wiring is a configuration bug we want to
        // catch at app-host build time, not at deploy time when nothing comes up.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envA = builder.AddKubernetesEnvironment("env-a");
        var envB = builder.AddKubernetesEnvironment("env-b");

        var issuerInA = envA.AddCertManager("cert-manager")
            .AddIssuer("le-prod")
            .WithLetsEncryptProduction("ops@contoso.com")
            .WithHttp01Solver();

        var gatewayInB = envB.AddGateway("public").WithGatewayClass("nginx");

        Assert.Throws<InvalidOperationException>(() => gatewayInB.WithTls(issuerInA));
    }

    [Fact]
    public void AddCertManager_RunMode_DoesNotRegisterResources()
    {
        // In run mode neither the wrapper nor its helm chart get added to the model
        // (helm install only runs at deploy time, and AddHelmChart already suppresses
        // its resource in run mode).
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var k8s = builder.AddKubernetesEnvironment("env");

        var certManager = k8s.AddCertManager("cert-manager");
        Assert.NotNull(certManager.Resource);

        var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        Assert.DoesNotContain(appModel.Resources, r => r is CertManagerResource);
    }
}
