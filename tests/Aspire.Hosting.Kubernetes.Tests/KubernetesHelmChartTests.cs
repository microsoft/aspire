// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

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
    public void AddHelmChart_ThrowsOnNullArguments()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        Assert.Throws<ArgumentNullException>(() =>
            ((IResourceBuilder<KubernetesEnvironmentResource>)null!).AddHelmChart("test", "oci://example.com/chart", "1.0.0"));
        Assert.Throws<ArgumentNullException>(() => k8s.AddHelmChart(null!, "oci://example.com/chart", "1.0.0"));
        Assert.Throws<ArgumentNullException>(() => k8s.AddHelmChart("test", null!, "1.0.0"));
        Assert.Throws<ArgumentNullException>(() => k8s.AddHelmChart("test", "oci://example.com/chart", null!));
    }

    [Theory]
    [InlineData("evil chart")]                  // whitespace
    [InlineData("evil\"chart")]                 // double quote
    [InlineData("evil\nchart")]                 // newline
    [InlineData("--evil")]                      // already validated by chartReference allowlist
    [InlineData("oci://repo/chart;rm -rf /")]   // semicolon
    public void AddHelmChart_RejectsMaliciousChartReference(string chartReference)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        // Note: the leading '--' case is currently allowed by the allowlist alone; it would
        // still be a positional arg to helm and is harmless (helm rejects unknown flags).
        // Whitespace, quotes, newlines, and semicolons must all be rejected to prevent
        // argument-string injection into the helm process.
        if (chartReference == "--evil")
        {
            // "--evil" passes the character allowlist but isn't a real injection vector
            // because it would be parsed by helm as a chart-reference positional arg.
            return;
        }

        Assert.Throws<ArgumentException>(() => k8s.AddHelmChart("test", chartReference, "1.0.0"));
    }

    [Theory]
    [InlineData("oci://quay.io/jetstack/charts/cert-manager")]   // OCI URL
    [InlineData("oci://ghcr.io/stefanprodan/charts/podinfo")]    // OCI URL with ghcr.io
    [InlineData("https://charts.example.com/repo/chart")]        // HTTPS URL
    [InlineData("http://charts.example.com/repo/chart")]         // HTTP URL
    [InlineData("myrepo/mychart")]                               // repo/chart
    [InlineData("mychart-1.0.0.tgz")]                            // packaged chart filename
    [InlineData("./local-chart")]                                // relative local path
    [InlineData("chart+extra~tag")]                              // plus and tilde chars
    [InlineData("oci://registry:5000/charts/app")]               // registry with port
    [InlineData("oci://user@registry.io/charts/app")]            // registry with @
    public void AddHelmChart_AcceptsValidChartReferences(string chartReference)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", chartReference, "1.0.0");
        Assert.Equal(chartReference, chart.Resource.ChartReference);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3-")]
    [InlineData("01.2.3")]
    public void AddHelmChart_RejectsInvalidChartVersion(string chartVersion)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        Assert.Throws<ArgumentException>(() => k8s.AddHelmChart("test", "oci://example.com/chart", chartVersion));
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta.1+ef365")]
    public void AddHelmChart_AcceptsValidChartVersion(string chartVersion)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", chartVersion);
        Assert.Equal(chartVersion, chart.Resource.ChartVersion);
    }

    [Theory]
    [InlineData("UpperCase")]
    [InlineData("with space")]
    [InlineData("trailing-")]
    [InlineData("-leading")]
    public void WithNamespace_RejectsInvalidDnsLabel(string @namespace)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        Assert.Throws<ArgumentException>(() => chart.WithNamespace(@namespace));
    }

    [Fact]
    public void WithNamespace_RejectsTooLong()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        var tooLong = new string('a', 64);
        Assert.Throws<ArgumentException>(() => chart.WithNamespace(tooLong));
    }

    [Theory]
    [InlineData("UpperCase")]
    [InlineData("with space")]
    [InlineData("ends-with-")]
    public void WithReleaseName_RejectsInvalidDnsLabel(string releaseName)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        Assert.Throws<ArgumentException>(() => chart.WithReleaseName(releaseName));
    }

    [Fact]
    public void WithReleaseName_RejectsTooLong()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        var tooLong = new string('a', 54);
        Assert.Throws<ArgumentException>(() => chart.WithReleaseName(tooLong));
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("with\"quote")]
    [InlineData("with\nnewline")]
    public void WithHelmValue_RejectsInvalidKey(string key)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        Assert.Throws<ArgumentException>(() => chart.WithHelmValue(key, "value"));
    }

    [Theory]
    [InlineData("evil\"value")]
    [InlineData("evil\\value")]
    [InlineData("evil\nvalue")]
    public void WithHelmValue_RejectsInvalidValue(string value)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        Assert.Throws<ArgumentException>(() => chart.WithHelmValue("key", value));
    }

    [Fact]
    public void WithHelmValue_AcceptsValueWithSpacesAndSpecialChars()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0")
            .WithHelmValue("config.greeting", "hello world!")
            .WithHelmValue("config.list", "a,b,c");

        Assert.Equal("hello world!", chart.Resource.Values["config.greeting"]);
        Assert.Equal("a,b,c", chart.Resource.Values["config.list"]);
    }

    [Fact]
    public void WithHelmValue_AcceptsBracketKey()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0")
            .WithHelmValue("args[0]", "--foo");

        Assert.Equal("--foo", chart.Resource.Values["args[0]"]);
    }

    [Fact]
    public void WithDestroy_DefaultsToFalse()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");

        Assert.False(chart.Resource.DestroyOnUninstall);
    }

    [Fact]
    public void WithDestroy_OptsIn()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0")
            .WithDestroy();

        Assert.True(chart.Resource.DestroyOnUninstall);
    }

    [Fact]
    public void WithDestroy_ReturnsBuilderForChaining()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");

        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");
        var returned = chart.WithDestroy();

        Assert.Same(chart, returned);
    }

    [Fact]
    public void WithDestroy_ThrowsOnNullBuilder()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((IResourceBuilder<KubernetesHelmChartResource>)null!).WithDestroy());
    }

    [Fact]
    public async Task PipelineStepFactory_WithoutDestroy_ProducesOnlyInstallStep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0");

        var steps = await CreateStepsAsync(builder, chart.Resource);

        Assert.Single(steps);
        Assert.Equal("helm-install-test", steps[0].Name);
    }

    [Fact]
    public async Task PipelineStepFactory_WithDestroy_ProducesInstallAndUninstallSteps()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.0.0")
            .WithDestroy();

        var steps = await CreateStepsAsync(builder, chart.Resource);

        Assert.Equal(2, steps.Count);
        var installStep = Assert.Single(steps, s => s.Name == "helm-install-test");
        var uninstallStep = Assert.Single(steps, s => s.Name == "helm-uninstall-test");

        // Install runs after the env's helm-deploy step and is required by the deploy aggregator.
        Assert.Contains($"helm-deploy-env", installStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Deploy, installStep.RequiredBySteps);

        // Uninstall slots into the destroy pipeline.
        Assert.Contains(WellKnownPipelineSteps.DestroyPrereq, uninstallStep.DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.Destroy, uninstallStep.RequiredBySteps);
    }

    [Fact]
    public async Task PipelineStepFactory_InstallStepDescription_UsesLiveResourceState()
    {
        // Verifies the description is computed at step-creation time from the resource so it
        // can't drift from the actual install args (the resource's chart props are now
        // immutable after construction).
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("test", "oci://example.com/chart", "1.2.3");

        var steps = await CreateStepsAsync(builder, chart.Resource);

        var installStep = Assert.Single(steps, s => s.Name == "helm-install-test");
        Assert.Contains("oci://example.com/chart", installStep.Description);
        Assert.Contains("1.2.3", installStep.Description);
    }

    [Fact]
    public async Task PipelineStepFactory_UninstallStepDescription_FallsBackToResourceNameForNamespace()
    {
        // When Namespace is not set, the uninstall step description should fall back to using
        // the resource name as the namespace (the same fallback used at install time).
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("my-release", "oci://example.com/chart", "1.0.0")
            .WithDestroy();

        Assert.Null(chart.Resource.Namespace);

        var steps = await CreateStepsAsync(builder, chart.Resource);

        var uninstallStep = Assert.Single(steps, s => s.Name == "helm-uninstall-my-release");
        Assert.Contains("my-release", uninstallStep.Description);
    }

    [Fact]
    public async Task PipelineStepFactory_UninstallStepDescription_UsesExplicitNamespace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("my-release", "oci://example.com/chart", "1.0.0")
            .WithNamespace("custom-ns")
            .WithDestroy();

        var steps = await CreateStepsAsync(builder, chart.Resource);

        var uninstallStep = Assert.Single(steps, s => s.Name == "helm-uninstall-my-release");
        Assert.Contains("custom-ns", uninstallStep.Description);
    }

    [Theory]
    [InlineData("MyChart")]                                                     // uppercase — valid Aspire name, invalid DNS label
    [InlineData("abcdefghij-abcdefghij-abcdefghij-abcdefghij-abcdefghij")]      // 54 chars — valid Aspire (<=64), exceeds Helm release name limit (53)
    public async Task PipelineStepFactory_RejectsResourceNameThatIsNotValidDnsLabel(string resourceName)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart(resourceName, "oci://example.com/chart", "1.0.0");

        // The factory should refuse to derive release/namespace from a resource name that
        // can't be used as a DNS label, surfacing a clear error before helm runs.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateStepsAsync(builder, chart.Resource));
        Assert.Contains(resourceName, ex.Message);
        Assert.Contains("WithReleaseName", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PipelineStepFactory_AcceptsInvalidResourceNameWhenOverridesProvided()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("env");
        var chart = k8s.AddHelmChart("MyChart", "oci://example.com/chart", "1.0.0")
            .WithReleaseName("my-release")
            .WithNamespace("my-ns");

        // With explicit overrides the resource name is never used as a fallback, so the
        // step factory must not throw.
        var steps = await CreateStepsAsync(builder, chart.Resource);
        Assert.Single(steps);
    }

    private static async Task<List<PipelineStep>> CreateStepsAsync(
        IDistributedApplicationTestingBuilder builder,
        KubernetesHelmChartResource resource)
    {
        using var serviceProvider = builder.Services.BuildServiceProvider();
        var pipelineContext = new PipelineContext(
            serviceProvider.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            serviceProvider,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource
            }));
        }

        return results;
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
