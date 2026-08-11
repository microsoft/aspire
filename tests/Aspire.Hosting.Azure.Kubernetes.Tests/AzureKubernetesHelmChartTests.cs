// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesHelmChartTests
{
    [Fact]
    public void AksAddHelmChart_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0");

        // The chart should be a child of the inner K8S environment, not the AKS environment.
        Assert.IsType<KubernetesHelmChartResource>(chart.Resource);
        Assert.IsType<KubernetesEnvironmentResource>(chart.Resource.Parent);
    }

    [Fact]
    public void AksAddHelmChart_BasicProperties()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0");

        Assert.Equal("cert-manager", chart.Resource.Name);
        Assert.Equal("oci://quay.io/jetstack/charts/cert-manager", chart.Resource.ChartReference);
        Assert.Equal("1.17.0", chart.Resource.ChartVersion);
    }

    [Fact]
    public void AksAddHelmChart_WithHelmValue_StoresValues()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0")
            .WithHelmValue("crds.enabled", "true")
            .WithHelmValue("config.enableGatewayAPI", "true");

        Assert.Equal(2, chart.Resource.Values.Count);
        Assert.Equal("true", chart.Resource.Values["crds.enabled"]);
        Assert.Equal("true", chart.Resource.Values["config.enableGatewayAPI"]);
    }

    [Fact]
    public void AksAddHelmChart_WithNamespace_SetsNamespace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0")
            .WithNamespace("ingress-nginx");

        Assert.Equal("ingress-nginx", chart.Resource.Namespace);
    }

    [Fact]
    public void AksAddHelmChart_WithReleaseName_SetsReleaseName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0")
            .WithReleaseName("my-nginx");

        Assert.Equal("my-nginx", chart.Resource.ReleaseName);
    }

    [Fact]
    public void AksAddHelmChart_WithDestroy_OptsIn()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1")
            .WithDestroy();

        Assert.True(chart.Resource.DestroyOnUninstall);
    }

    [Fact]
    public void AksAddHelmChart_DestroyDefaultsToFalse()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var chart = aks.AddHelmChart("podinfo", "oci://ghcr.io/stefanprodan/charts/podinfo", "6.7.1");

        Assert.False(chart.Resource.DestroyOnUninstall);
    }

    [Fact]
    public void AksAddHelmChart_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((IResourceBuilder<AzureKubernetesEnvironmentResource>)null!)
            .AddHelmChart("test", "oci://example.com/chart", "1.0.0"));
    }

    [Theory]
    [InlineData(null, "oci://example.com/chart", "1.0.0")]
    [InlineData("", "oci://example.com/chart", "1.0.0")]
    [InlineData("test", null, "1.0.0")]
    [InlineData("test", "", "1.0.0")]
    [InlineData("test", "oci://example.com/chart", null)]
    [InlineData("test", "oci://example.com/chart", "")]
    public void AksAddHelmChart_ThrowsOnNullOrEmptyArgs(string? name, string? chartReference, string? chartVersion)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.ThrowsAny<ArgumentException>(() => aks.AddHelmChart(name!, chartReference!, chartVersion!));
    }

    [Fact]
    public void AksAddHelmChart_RejectsInvalidChartVersion()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Throws<ArgumentException>(() =>
            aks.AddHelmChart("test", "oci://example.com/chart", "not-a-semver"));
    }

    [Fact]
    public void AksAddHelmChart_RejectsMaliciousChartReference()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        Assert.Throws<ArgumentException>(() =>
            aks.AddHelmChart("test", "evil chart\"", "1.0.0"));
    }
}
