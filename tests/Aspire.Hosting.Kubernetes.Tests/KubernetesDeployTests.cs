// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesDeployTests(ITestOutputHelper output)
{
    [Fact]
    public void AddKubernetesEnvironment_AddsDefaultHelmEngine()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env");

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<KubernetesDeploymentEngineAnnotation>(out var annotation));
        Assert.NotNull(annotation.CreateSteps);
    }

    [Fact]
    public void WithHelm_ReplacesExistingEngine()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envBuilder = builder.AddKubernetesEnvironment("env");

        // The default engine is added by AddKubernetesEnvironment
        // Calling WithHelm should replace it
        envBuilder.WithHelm();

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        var annotations = env.Annotations.OfType<KubernetesDeploymentEngineAnnotation>().ToList();
        Assert.Single(annotations);
    }

    [Fact]
    public void WithHelm_ConfiguresNamespace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithNamespace("production"));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var nsAnnotation));
        Assert.NotNull(nsAnnotation.Namespace);
    }

    [Fact]
    public void WithHelm_ConfiguresReleaseName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithReleaseName("my-release"));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out var annotation));
        Assert.NotNull(annotation.ReleaseName);
    }

    [Fact]
    public void WithHelm_ConfiguresChartVersion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithChartVersion("2.0.0"));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<HelmChartVersionAnnotation>(out var annotation));
        Assert.NotNull(annotation.Version);
    }

    [Fact]
    public void WithHelm_ConfiguresAllSettings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                helm.WithNamespace("staging");
                helm.WithReleaseName("my-app");
                helm.WithChartVersion("1.2.3");
            });

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out _));
        Assert.True(env.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out _));
        Assert.True(env.TryGetLastAnnotation<HelmChartVersionAnnotation>(out _));
    }

    [Fact]
    public void WithHelm_NamespaceAcceptsParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var nsParam = builder.AddParameter("k8s-namespace");

        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithNamespace(nsParam));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<KubernetesNamespaceAnnotation>(out var annotation));
        Assert.NotNull(annotation.Namespace);
    }

    [Fact]
    public void WithHelm_ReleaseNameAcceptsParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var releaseParam = builder.AddParameter("helm-release");

        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithReleaseName(releaseParam));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<HelmReleaseNameAnnotation>(out var annotation));
        Assert.NotNull(annotation.ReleaseName);
    }

    [Fact]
    public void WithHelm_ChartVersionAcceptsParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var versionParam = builder.AddParameter("chart-version");

        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithChartVersion(versionParam));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<HelmChartVersionAnnotation>(out var annotation));
        Assert.NotNull(annotation.Version);
    }

    [Fact]
    public void WithHelm_RepeatedCallsReplaceAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm => helm.WithNamespace("first"))
            .WithHelm(helm => helm.WithNamespace("second"));

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        // Should have only one namespace annotation (replaced, not stacked)
        var nsAnnotations = env.Annotations.OfType<KubernetesNamespaceAnnotation>().ToList();
        Assert.Single(nsAnnotations);
    }

    [Fact]
    public void WithHelm_WithoutCallback_StillAddsEngine()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm();

        var app = builder.Build();
        var env = app.Services.GetRequiredService<DistributedApplicationModel>()
            .Resources.OfType<KubernetesEnvironmentResource>().Single();

        Assert.True(env.TryGetLastAnnotation<KubernetesDeploymentEngineAnnotation>(out _));
    }

    [Fact]
    public async Task HelmDeployStepIsCreatedInDiagnosticsMode()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify helm-deploy step exists
        Assert.Contains(logs, msg => msg.Contains("helm-deploy-env"));

        // Verify publish step exists
        Assert.Contains(logs, msg => msg.Contains("publish-env"));

        // Verify prepare step exists
        Assert.Contains(logs, msg => msg.Contains("prepare-env"));
    }

    [Fact]
    public async Task HelmDeployStep_DependsOnPublishStep()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify prepare-env depends on publish-env
        var prepareLines = logs.Where(l => l.Contains("prepare-env")).ToList();
        Assert.Contains(prepareLines, msg => msg.Contains("publish-env"));

        // Verify helm-deploy-env depends on prepare-env
        var helmDeployLines = logs.Where(l => l.Contains("helm-deploy-env")).ToList();
        Assert.Contains(helmDeployLines, msg => msg.Contains("prepare-env"));
    }

    [Fact]
    public async Task HelmDeployStep_DependsOnPushSteps_WhenRegistryConfigured()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");
        builder.AddProject<Projects.ServiceA>("api")
            .PublishAsDockerFile();

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify push-api step exists
        Assert.Contains(logs, msg => msg.Contains("push-api"));

        // Verify helm-deploy-env depends on push-api
        var helmDeployLines = logs.Where(l => l.Contains("helm-deploy-env")).ToList();
        Assert.Contains(helmDeployLines, msg => msg.Contains("push-api"));
    }

    [Fact]
    public async Task PrintSummaryStepIsCreated()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080);

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify print-summary step exists for the api resource
        Assert.Contains(logs, msg => msg.Contains("print-api-summary"));

        // Verify print-summary depends on helm-deploy
        var printLines = logs.Where(l => l.Contains("print-api-summary")).ToList();
        Assert.Contains(printLines, msg => msg.Contains("helm-deploy-env"));
    }

    [Fact]
    public async Task HelmUninstallStepIsCreated()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify helm-uninstall step exists
        Assert.Contains(logs, msg => msg.Contains("helm-uninstall-env"));
    }

    [Fact]
    public async Task MultipleContainersGenerateMultiplePrintSummarySteps()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080);
        builder.AddContainer("web", "mywebimage")
            .WithHttpEndpoint(targetPort: 3000);

        using var app = builder.Build();
        await app.RunAsync();

        var logs = mockActivityReporter.LoggedMessages
            .Where(s => s.StepTitle == "diagnostics")
            .Select(s => s.Message)
            .ToList();

        output.WriteLine("Diagnostics logs:");
        foreach (var log in logs)
        {
            output.WriteLine($"  {log}");
        }

        // Verify both print-summary steps exist
        Assert.Contains(logs, msg => msg.Contains("print-api-summary"));
        Assert.Contains(logs, msg => msg.Contains("print-web-summary"));
    }

    [Fact]
    public void WithHelm_ThrowsOnNullBuilder()
    {
        IResourceBuilder<KubernetesEnvironmentResource> builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.WithHelm());
    }

    [Fact]
    public void HelmChartConfiguration_WithNamespace_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithNamespace(string.Empty));
            });
    }

    [Fact]
    public void HelmChartConfiguration_WithReleaseName_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithReleaseName(string.Empty));
            });
    }

    [Fact]
    public void HelmChartConfiguration_WithChartVersion_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithChartVersion(string.Empty));
            });
    }

    [Fact]
    public async Task ContainerRegistryIsWiredIntoDeploymentTarget()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Diagnostics);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");
        var containerBuilder = builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080);

        using var app = builder.Build();

        // Get the resource before running (service provider gets disposed after run)
        var container = containerBuilder.Resource;

        await app.RunAsync();

        Assert.True(container.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var dta));
        Assert.NotNull(dta.ComputeEnvironment);
    }
}
