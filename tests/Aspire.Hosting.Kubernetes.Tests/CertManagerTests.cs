// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Kubernetes.Tests;

public class CertManagerTests
{
    private const string TestHmacKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh_-";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WithAcmeServer_ParameterizedUrlResolvesAtDeployTime(bool parameterizedEmail)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var serverUrl = builder.AddParameter("acmeurl");
        var email = builder.AddParameter("acmeemail");
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");
        var issuer = certManager.AddIssuer("parameterized-acme").WithHttp01Solver();

        if (parameterizedEmail)
        {
            issuer.WithAcmeServer(serverUrl, email);
        }
        else
        {
            issuer.WithAcmeServer(serverUrl, "ops@contoso.com");
        }
        var spec = Assert.IsType<CertManagerAcmeIssuerSpec>(issuer.Resource.Spec);
        Assert.Same(serverUrl.Resource, Assert.Single(spec.ServerUrl.ValueProviders));

        builder.Configuration["Parameters:acmeurl"] = "https://acme.example.com/directory";
        builder.Configuration["Parameters:acmeemail"] = "ops@contoso.com";
        using var app = builder.Build();
        var yaml = await CertManagerExtensions.BuildClusterIssuerManifestAsync(
            app.Services.GetRequiredService<DistributedApplicationModel>(), certManager.Resource, issuer.Resource,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        await Verify(yaml, "yaml").UseParameters(parameterizedEmail);
    }

    [Fact]
    public void WithAcmeServer_ParameterizedUrlValidatesArguments()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var serverUrl = builder.AddParameter("acmeurl");
        var email = builder.AddParameter("acmeemail");
        var issuer = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager").AddIssuer("acme");

        Assert.Throws<ArgumentNullException>(() => CertManagerExtensions.WithAcmeServer(null!, serverUrl, "ops@contoso.com"));
        Assert.Throws<ArgumentNullException>(() => CertManagerExtensions.WithAcmeServer(null!, serverUrl, email));
        Assert.Throws<ArgumentNullException>(() => issuer.WithAcmeServer((IResourceBuilder<ParameterResource>)null!, "ops@contoso.com"));
        Assert.Throws<ArgumentNullException>(() => issuer.WithAcmeServer((IResourceBuilder<ParameterResource>)null!, email));
        Assert.Throws<ArgumentNullException>(() => issuer.WithAcmeServer(serverUrl, (string)null!));
        Assert.Throws<ArgumentException>(() => issuer.WithAcmeServer(serverUrl, ""));
        Assert.Throws<ArgumentNullException>(() => issuer.WithAcmeServer(serverUrl, (IResourceBuilder<ParameterResource>)null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithExternalAccountBinding_StoresCredentialsWithoutResolvingParameters(bool parameterizedKeyId)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var issuer = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager").AddIssuer("eab");
        var hmacKey = builder.AddParameter("eab-hmac", secret: true);
        var keyId = builder.AddParameter("eab-key-id");

        var result = parameterizedKeyId
            ? issuer.WithExternalAccountBinding(keyId, hmacKey)
            : issuer.WithExternalAccountBinding("provider-key-id", hmacKey);
        issuer.WithAcmeServer("https://acme.example.com/directory", "ops@contoso.com");

        Assert.Same(issuer, result);
        var binding = Assert.IsType<CertManagerExternalAccountBinding>(issuer.Resource.ExternalAccountBinding);
        Assert.Same(hmacKey.Resource, binding.HmacKey);
        Assert.Equal(parameterizedKeyId ? "{0}" : "provider-key-id", binding.KeyId.Format);
        if (parameterizedKeyId)
        {
            Assert.Same(keyId.Resource, Assert.Single(binding.KeyId.ValueProviders));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithExternalAccountBinding_RejectsNonSecretHmacKey(bool parameterizedKeyId)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var issuer = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager").AddIssuer("eab");
        var hmacKey = builder.AddParameter("eab-hmac");
        var keyId = builder.AddParameter("eab-key-id");

        var exception = Assert.Throws<ArgumentException>(() => parameterizedKeyId
            ? issuer.WithExternalAccountBinding(keyId, hmacKey)
            : issuer.WithExternalAccountBinding("provider-key-id", hmacKey));
        Assert.Equal("hmacKey", exception.ParamName);
        Assert.Null(issuer.Resource.ExternalAccountBinding);
    }

    [Fact]
    public void WithExternalAccountBinding_ValidatesArguments()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var issuer = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager").AddIssuer("eab");
        var hmacKey = builder.AddParameter("eab-hmac", secret: true);
        var keyId = builder.AddParameter("eab-key-id");

        Assert.Throws<ArgumentNullException>(() => CertManagerExtensions.WithExternalAccountBinding(null!, "key", hmacKey));
        Assert.Throws<ArgumentNullException>(() => CertManagerExtensions.WithExternalAccountBinding(null!, keyId, hmacKey));
        Assert.Throws<ArgumentNullException>(() => issuer.WithExternalAccountBinding((string)null!, hmacKey));
        Assert.Throws<ArgumentException>(() => issuer.WithExternalAccountBinding("", hmacKey));
        Assert.Throws<ArgumentNullException>(() => issuer.WithExternalAccountBinding((IResourceBuilder<ParameterResource>)null!, hmacKey));
        Assert.Throws<ArgumentNullException>(() => issuer.WithExternalAccountBinding("key", null!));
        Assert.Throws<ArgumentNullException>(() => issuer.WithExternalAccountBinding(keyId, null!));
    }

    [Theory]
    [InlineData(null, null, null, "cert-manager-chart")]
    [InlineData("controllers", null, null, "controllers")]
    [InlineData("controllers", "accounts", null, "accounts")]
    [InlineData("controllers", "", null, "controllers")]
    [InlineData("controllers", null, "chart-override", "chart-override")]
    [InlineData("controllers", null, "", "controllers")]
    public void ExternalAccountBinding_UsesClusterResourceNamespace(
        string? installNamespace, string? clusterNamespace, string? chartNamespace, string expected)
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");
        certManager.Resource.HelmChart.Namespace = installNamespace;
        if (clusterNamespace is not null)
        {
            certManager.Resource.HelmChart.Values["clusterResourceNamespace"] = clusterNamespace;
        }
        if (chartNamespace is not null)
        {
            certManager.Resource.HelmChart.Values["namespace"] = chartNamespace;
        }

        Assert.Equal(expected, CertManagerExtensions.GetClusterResourceNamespace(certManager.Resource));
    }

    [Fact]
    public void ExternalAccountBinding_RejectsAmbiguousNamespaceOverride()
    {
        var builder = TestDistributedApplicationBuilder.Create();
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");
        certManager.Resource.HelmChart.Values["extraArgs[0]"] = "--cluster-resource-namespace=accounts";

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CertManagerExtensions.GetClusterResourceNamespace(certManager.Resource));
        Assert.Contains("'clusterResourceNamespace' Helm value", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssuerPipeline_ValidatesSpecAndSolverBeforeInvokingKubectl(bool withSpec)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");
        var issuer = certManager.AddIssuer("eab");
        if (withSpec)
        {
            issuer.WithAcmeServer("https://acme.example.com/directory", "ops@contoso.com");
        }
        using var app = builder.Build();
        var steps = await PipelineStepTestHelpers.CreateStepsAsync(app.Services, certManager.Resource);
        var apply = Assert.Single(steps, step => step.Name == "cm-issuer-apply-eab");
        Assert.Contains("helm-install-cert-manager-chart", apply.DependsOnSteps);
        var delete = Assert.Single(steps, step => step.Name == "cm-issuer-delete-eab");
        Assert.Contains("helm-uninstall-cert-manager-chart", delete.RequiredBySteps);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PipelineStepTestHelpers.RunStepAsync(app.Services, apply));
        Assert.Equal(withSpec
            ? "ClusterIssuer 'eab' has no solvers configured. Add at least one solver via WithHttp01Solver()."
            : "ClusterIssuer 'eab' has no spec. Configure it with WithAcmeServer().", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("issuer-eab:aspire-cert-manager")]
    public void ExternalAccountBinding_AcceptsMissingOrOwnedSecret(string ownership)
    {
        CertManagerExtensions.ValidateExternalAccountBindingSecretOwnership("issuer-eab", ownership);
    }

    [Theory]
    [InlineData("issuer-eab:")]
    [InlineData("issuer-eab:another-manager")]
    public void ExternalAccountBinding_RejectsUnmanagedSecret(string ownership)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CertManagerExtensions.ValidateExternalAccountBindingSecretOwnership("issuer-eab", ownership));
    }

    [Fact]
    public async Task BuildExternalAccountBindingSecretApplySpec_UsesStdinAndPreservesBase64UrlText()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Configuration["Parameters:eab-hmac"] = TestHmacKey;
        var hmacKey = builder.AddParameter("eab-hmac", secret: true);
        var issuer = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager").AddIssuer("Eab-Issuer")
            .WithExternalAccountBinding("fixture-key-id", hmacKey);

        var spec = await CertManagerExtensions.BuildExternalAccountBindingSecretApplySpecAsync(
            issuer.Resource, TestContext.Current.CancellationToken);

        Assert.Equal(["apply", "--server-side", "--field-manager=aspire-cert-manager", "-f", "-"], spec.ArgumentList);
        Assert.Null(spec.Arguments);
        Assert.Null(spec.OnOutputData);
        Assert.Null(spec.OnErrorData);
        Assert.False(spec.ThrowOnNonZeroReturnCode);
        Assert.Equal(0, spec.RetainedOutputLineCount);
        using var document = JsonDocument.Parse(spec.StandardInputContent!);
        var encodedKey = document.RootElement.GetProperty("data").GetProperty("hmac-key").GetString()!;
        Assert.Equal(TestHmacKey, Encoding.UTF8.GetString(Convert.FromBase64String(encodedKey)));
        await Verify(spec.StandardInputContent!, "json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildClusterIssuerManifest_ExternalAccountBindingEscapesKeyId(bool parameterizedKeyId)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        const string keyIdValue = "fixture: \"account\"\n#key";
        builder.Configuration["Parameters:eab-key-id"] = keyIdValue;
        var keyId = builder.AddParameter("eab-key-id");
        var hmacKey = builder.AddParameter("eab-hmac", secret: true);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");
        var issuer = certManager.AddIssuer("eab")
            .WithAcmeServer("https://acme.example.com/directory", "ops@contoso.com")
            .WithHttp01Solver();
        if (parameterizedKeyId)
        {
            issuer.WithExternalAccountBinding(keyId, hmacKey);
        }
        else
        {
            issuer.WithExternalAccountBinding(keyIdValue, hmacKey);
        }

        using var app = builder.Build();
        var yaml = await CertManagerExtensions.BuildClusterIssuerManifestAsync(
            app.Services.GetRequiredService<DistributedApplicationModel>(), certManager.Resource, issuer.Resource,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        await Verify(yaml, "yaml").UseParameters(parameterizedKeyId);
    }

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
    public void AddIssuer_WithAcmeServerAndHttp01_PopulatesSpecAndSolver()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var certManager = k8s.AddCertManager("cert-manager");

        var issuer = certManager.AddIssuer("letsencrypt-prod")
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
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
    public void WithAcmeServer_UsesStagingDirectoryUrl()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var certManager = builder.AddKubernetesEnvironment("env").AddCertManager("cert-manager");

        var issuer = certManager.AddIssuer("le-staging")
            .WithAcmeServer("https://acme-staging-v02.api.letsencrypt.org/directory", "ops@contoso.com");

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
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
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
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
            .WithHttp01Solver();

        // A gateway that adopts this issuer must end up referenced as a parentRef on the
        // generated solver, so cert-manager's HTTP-01 HTTPRoute can attach to it. The gateway
        // needs a route, otherwise it is skipped during materialization and referencing it
        // would produce a parentRef to a Gateway that never exists in the cluster.
        var api = builder.AddContainer("myapi", "nginx")
            .WithHttpEndpoint(targetPort: 8080)
            .WithExternalHttpEndpoints();

        k8s.AddGateway("PUBLIC-GW")
            .WithGatewayClass("nginx")
            .WithRoute("/", api.GetEndpoint("http"))
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
        Assert.Contains("server: \"https://acme-v02.api.letsencrypt.org/directory\"", yaml);
        Assert.Contains("email: \"ops@contoso.com\"", yaml);
        Assert.Contains("name: letsencrypt-prod-account-key", yaml);
        Assert.Contains("- http01:", yaml);
        Assert.Contains("gatewayHTTPRoute:", yaml);
        // Gateway parentRef must also be lowercase to match the actual emitted Gateway name.
        Assert.Contains("name: public-gw", yaml);
    }

    [Fact]
    public async Task BuildClusterIssuerManifest_RouteLessGateway_IsNotEmittedAsParentRef()
    {
        // A gateway with no routes is skipped during materialization, so emitting it as a
        // solver parentRef would point cert-manager's HTTP-01 HTTPRoute at a Gateway that
        // never exists in the cluster, leaving Certificates stuck in 'Pending' forever.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager("cert-manager")
            .AddIssuer("le-prod")
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
            .WithHttp01Solver();

        k8s.AddGateway("public")
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

        // The solver is still emitted, but with no parentRefs block at all, which is also what
        // makes the "no routed Gateway adopts this issuer" warning fire.
        Assert.Contains("- http01:", yaml);
        Assert.Contains("gatewayHTTPRoute:", yaml);
        Assert.DoesNotContain("parentRefs:", yaml);
        Assert.DoesNotContain("name: public", yaml);
    }

    [Fact]
    public async Task BuildClusterIssuerManifest_RouteLessGateway_WarnsThatChallengesCannotBeSatisfied()
    {
        // Without a parentRef the manifest is still valid YAML and cert-manager accepts it, so the
        // warning is the only thing that tells the user why their Certificates never leave
        // 'Pending'. Assert it explicitly so it cannot be dropped without a test failing.
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var issuer = k8s.AddCertManager("cert-manager")
            .AddIssuer("le-prod")
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
            .WithHttp01Solver();

        k8s.AddGateway("public")
            .WithGatewayClass("nginx")
            .WithTls(issuer);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var certManagerResource = model.Resources.OfType<CertManagerResource>().Single();
        var issuerResource = certManagerResource.Issuers.Single();

        var testSink = new TestSink();
        using var loggerProvider = new TestLoggerProvider(testSink);

        await CertManagerExtensions.BuildClusterIssuerManifestAsync(
            model,
            certManagerResource,
            issuerResource,
            loggerProvider.CreateLogger("test"),
            TestContext.Current.CancellationToken);

        var warning = Assert.Single(testSink.Writes, w => w.LogLevel == LogLevel.Warning);

        Assert.Equal(
            "ClusterIssuer 'le-prod' has an HTTP-01 solver but no Gateway in environment 'env' is both annotated with " +
            "cert-manager.io/cluster-issuer=le-prod and configured with at least one route. cert-manager will not be able to " +
            "satisfy ACME challenges until at least one routed Gateway adopts this issuer (e.g. via WithRoute(...) and WithTls(issuer)).",
            warning.Message);
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
            .WithAcmeServer("https://acme-v02.api.letsencrypt.org/directory", "ops@contoso.com")
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
