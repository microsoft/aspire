// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesHelmChartTests
{
    [Fact]
    public void AddHelmChart_CreatesResourceWithCorrectProperties()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0");

        Assert.Equal("cert-manager", chart.Resource.Name);
        Assert.Equal("oci://quay.io/jetstack/charts/cert-manager", chart.Resource.ChartReference);
        Assert.Equal("1.17.0", chart.Resource.ChartVersion);
        Assert.Equal("env", chart.Resource.Parent.Name);
    }

    [Fact]
    public void AddHelmChart_WithHelmValues_StoresValues()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0")
            .WithHelmValue("crds.enabled", "true")
            .WithHelmValue("config.enableGatewayAPI", "true");

        Assert.Equal(2, chart.Resource.Values.Count);
        Assert.Equal("true", chart.Resource.Values["crds.enabled"]);
        Assert.Equal("true", chart.Resource.Values["config.enableGatewayAPI"]);
    }

    [Fact]
    public void AddHelmChart_WithNamespace_SetsNamespace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0")
            .WithNamespace("ingress-nginx");

        Assert.Equal("ingress-nginx", chart.Resource.Namespace);
    }

    [Fact]
    public void AddHelmChart_WithReleaseName_SetsReleaseName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0")
            .WithReleaseName("my-nginx");

        Assert.Equal("my-nginx", chart.Resource.ReleaseName);
    }

    [Fact]
    public void AddHelmChart_DefaultsNamespaceAndReleaseNameToNull()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("test", "oci://example.com/charts/test", "1.0.0");

        Assert.Null(chart.Resource.Namespace);
        Assert.Null(chart.Resource.ReleaseName);
    }

    [Fact]
    public void AddHelmChart_HasPipelineStepAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0");

        Assert.True(
            chart.Resource.TryGetAnnotationsOfType<PipelineStepAnnotation>(out var annotations),
            "Helm chart resource should have a PipelineStepAnnotation");
        Assert.Single(annotations);
    }

    [Fact]
    public void AddHelmChart_MultipleCharts_AllRegistered()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart1 = k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0");
        var chart2 = k8s.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0");

        Assert.NotEqual(chart1.Resource.Name, chart2.Resource.Name);
        Assert.Equal("env", chart1.Resource.Parent.Name);
        Assert.Equal("env", chart2.Resource.Parent.Name);
    }

    [Fact]
    public void AddHelmChart_ThrowsOnNullOrEmptyArguments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        Assert.Throws<ArgumentException>(() => k8s.AddHelmChart("", "oci://example.com/chart", "1.0.0"));
        Assert.Throws<ArgumentException>(() => k8s.AddHelmChart("test", "", "1.0.0"));
        Assert.Throws<ArgumentException>(() => k8s.AddHelmChart("test", "oci://example.com/chart", ""));
    }

    [Fact]
    public void WithHelmValue_OverwritesExistingKey()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0")
            .WithHelmValue("key", "value1")
            .WithHelmValue("key", "value2");

        Assert.Single(chart.Resource.Values);
        Assert.Equal("value2", chart.Resource.Values["key"]);
    }
}
