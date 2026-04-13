// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
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

        Assert.NotNull(env.DeploymentEngineStepsFactory);
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

        Assert.NotNull(env.DeploymentEngineStepsFactory);
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

        Assert.NotNull(env.DeploymentEngineStepsFactory);
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
    public async Task HelmUninstallStep_RequiredByDestroy()
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

        // Verify helm-uninstall-env depends on destroy-helm-env (the prompt layer)
        var helmUninstallLines = logs.Where(l => l.Contains("helm-uninstall-env")).ToList();
        Assert.Contains(helmUninstallLines, msg => msg.Contains("destroy-helm-env"));
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
    public void HelmChartOptions_WithNamespace_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithNamespace(string.Empty));
            });
    }

    [Fact]
    public void HelmChartOptions_WithReleaseName_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithReleaseName(string.Empty));
            });
    }

    [Fact]
    public void HelmChartOptions_WithChartVersion_ThrowsOnEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithChartVersion(string.Empty));
            });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("my_namespace")]
    [InlineData("-staging")]
    [InlineData("staging-")]
    public void HelmChartOptions_WithNamespace_ThrowsOnInvalidName(string value)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithNamespace(value));
            });
    }

    [Theory]
    [InlineData("MyRelease")]
    [InlineData("release_name")]
    [InlineData("-release")]
    [InlineData("release-")]
    public void HelmChartOptions_WithReleaseName_ThrowsOnInvalidName(string value)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithReleaseName(value));
            });
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("release-candidate")]
    [InlineData("1.0")]
    public void HelmChartOptions_WithChartVersion_ThrowsOnInvalidVersion(string value)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddKubernetesEnvironment("env")
            .WithHelm(helm =>
            {
                Assert.Throws<ArgumentException>(() => helm.WithChartVersion(value));
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

    [Fact]
    public async Task PrepareAsync_ResolvesSecretParameterValues()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        // Write a values.yaml with empty secret placeholders
        var valuesYaml = """
            parameters: {}
            secrets:
              myapp:
                password: ""
            config: {}
            """;
        await File.WriteAllTextAsync(Path.Combine(outputPath, "values.yaml"), valuesYaml);

        var environment = new KubernetesEnvironmentResource("env");

        // Create a parameter with a known value and add it to CapturedHelmValues
        var paramResource = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(
            TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish),
            "mypassword",
            special: false);

        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue(
                "secrets", "myapp", "password", paramResource));

        // Act
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert: values.env.yaml should exist with the resolved value
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.True(File.Exists(deployValuesPath), "values.env.yaml should be created");

        var content = await File.ReadAllTextAsync(deployValuesPath);
        Assert.Contains("secrets:", content);
        Assert.Contains("myapp:", content);
        Assert.Contains("password:", content);

        // The password should NOT be empty
        Assert.DoesNotContain("password: \"\"", content);
        Assert.DoesNotContain("password: ''", content);
    }

    [Fact]
    public async Task PrepareAsync_NoCapturedValues_DoesNotCreateDeployFile()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        await File.WriteAllTextAsync(Path.Combine(outputPath, "values.yaml"), "parameters: {}\nsecrets: {}\nconfig: {}");

        var environment = new KubernetesEnvironmentResource("env");

        // Act: no captured values
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert: no override file created
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.False(File.Exists(deployValuesPath), "values.env.yaml should not be created when there are no captured values");
    }

    [Fact]
    public async Task PrepareAsync_ResolvesMultipleParametersAcrossResources()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        await File.WriteAllTextAsync(Path.Combine(outputPath, "values.yaml"), "parameters: {}\nsecrets: {}\nconfig: {}");

        var environment = new KubernetesEnvironmentResource("env");
        var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create multiple parameters for different resources
        var param1 = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(appBuilder, "cache-password", special: false);
        var param2 = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(appBuilder, "db-password", special: false);

        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue("secrets", "cache", "password", param1));
        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue("secrets", "database", "password", param2));

        // Act
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert: both secrets should be in the deploy values file
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.True(File.Exists(deployValuesPath));

        var content = await File.ReadAllTextAsync(deployValuesPath);
        Assert.Contains("cache:", content);
        Assert.Contains("database:", content);
    }

    [Fact]
    public void ResolveHelmExpressions_SubstitutesDirectReferences()
    {
        var lookup = new Dictionary<string, string>
        {
            ["secrets.cache.password"] = "s3cret!"
        };

        var template = "{{ .Values.secrets.cache.password }}";
        var result = HelmDeploymentEngine.ResolveHelmExpressions(template, lookup);

        Assert.Equal("s3cret!", result);
    }

    [Fact]
    public void ResolveHelmExpressions_SubstitutesCompositeConnectionString()
    {
        var lookup = new Dictionary<string, string>
        {
            ["secrets.cache.password"] = "myP@ss"
        };

        var template = "cache:6379,password={{ .Values.secrets.cache.password }}";
        var result = HelmDeploymentEngine.ResolveHelmExpressions(template, lookup);

        Assert.Equal("cache:6379,password=myP@ss", result);
    }

    [Fact]
    public void ResolveHelmExpressions_PreservesUnresolvedExpressions()
    {
        var lookup = new Dictionary<string, string>();

        var template = "host={{ .Values.config.myapp.host }},password={{ .Values.secrets.cache.password }}";
        var result = HelmDeploymentEngine.ResolveHelmExpressions(template, lookup);

        // Unresolved expressions are preserved as-is
        Assert.Equal(template, result);
    }

    [Fact]
    public async Task PrepareAsync_ResolvesCrossResourceReferences()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        await File.WriteAllTextAsync(Path.Combine(outputPath, "values.yaml"), "parameters: {}\nsecrets: {}\nconfig: {}");

        var environment = new KubernetesEnvironmentResource("env");
        var appBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Direct secret parameter for cache resource
        var cachePassword = ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(appBuilder, "cache-password", special: false);
        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue("secrets", "cache", "password", cachePassword));

        // Cross-resource reference: server's connection string references cache's password
        environment.CapturedHelmCrossReferences.Add(
            new KubernetesEnvironmentResource.CapturedHelmCrossReference(
                "secrets", "server", "ConnectionStrings__cache",
                "cache:6379,password={{ .Values.secrets.cache.password }}"));

        // Act
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.True(File.Exists(deployValuesPath));

        var content = await File.ReadAllTextAsync(deployValuesPath);

        // Both the direct secret and the cross-reference should be present
        Assert.Contains("cache:", content);
        Assert.Contains("server:", content);

        // The cross-reference should contain the resolved password, not the Helm expression
        Assert.DoesNotContain("{{ .Values", content);
    }

    [Fact]
    public void DeployValuesFileName_IncludesEnvironmentName()
    {
        Assert.Equal("values.myenv.yaml", HelmDeploymentEngine.GetDeployValuesFileName("myenv"));
        Assert.Equal("values.k8s.yaml", HelmDeploymentEngine.GetDeployValuesFileName("k8s"));
    }

    [Fact]
    public async Task PublishCapturesSecretParameterMappings()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        var envBuilder = builder.AddKubernetesEnvironment("env");
        var password = builder.AddParameter("my-password", secret: true);

        builder.AddContainer("api", "myimage")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEnvironment("DB_PASSWORD", password);

        using var app = builder.Build();
        var env = envBuilder.Resource;

        await app.RunAsync();

        // After publish, secret parameter mappings should be captured
        Assert.NotEmpty(env.CapturedHelmValues);
        Assert.Contains(env.CapturedHelmValues, c =>
            c.Section == "secrets" &&
            c.Parameter.Name == "my-password");
    }

    [Fact]
    public async Task PublishCapturesImageReferencesForProjectResources()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");
        var envBuilder = builder.AddKubernetesEnvironment("env");

        builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var env = envBuilder.Resource;

        await app.RunAsync();

        // After publish, project resources should have image references captured for registry resolution
        Assert.NotEmpty(env.CapturedHelmImageReferences);
        Assert.Contains(env.CapturedHelmImageReferences, c =>
            c.Section == "parameters" &&
            c.ValueKey == "api_image" &&
            c.Resource.Name == "api");
    }

    [Fact]
    public void HelmValue_ImageResource_IsSetForProjectResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddKubernetesEnvironment("env");
        var project = builder.AddProject<Projects.ServiceA>("api");

        var app = builder.Build();
        var envResource = env.Resource;

        // Create a KubernetesResource to test GetContainerImageName
        var k8sResource = new KubernetesResource("api-k8s", project.Resource, envResource);
        var imageName = k8sResource.GetContainerImageName(project.Resource);

        // Should return a Helm expression
        Assert.Contains("{{ .Values.", imageName);

        // The Parameters dictionary should have the image entry with ImageResource set
        var imageParam = k8sResource.Parameters.Values.SingleOrDefault(p => p.ImageResource is not null);
        Assert.NotNull(imageParam);
        Assert.Same(project.Resource, imageParam.ImageResource);
    }

    [Fact]
    public void HelmValue_ImageResource_IsNotSetForContainerResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddKubernetesEnvironment("env");
        var container = builder.AddContainer("cache", "redis");

        var app = builder.Build();
        var envResource = env.Resource;

        // Create a KubernetesResource to test GetContainerImageName
        var k8sResource = new KubernetesResource("cache-k8s", container.Resource, envResource);
        var imageName = k8sResource.GetContainerImageName(container.Resource);

        // Container resources with existing images return the literal image name, not a Helm expression
        Assert.DoesNotContain("{{ .Values.", imageName);

        // No parameters should have ImageResource set (image is pre-existing, no build needed)
        Assert.DoesNotContain(k8sResource.Parameters.Values, p => p.ImageResource is not null);
    }

    [Fact]
    public async Task PrepareAsync_ResolvesImageReferencesWithRegistryPrefix()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        // Write a values.yaml with the default image placeholder
        var valuesYaml = """
            parameters:
              api:
                api_image: "api:latest"
            secrets: {}
            config: {}
            """;
        await File.WriteAllTextAsync(Path.Combine(outputPath, "values.yaml"), valuesYaml);

        // Set up a builder with a container registry and a project resource
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddContainerRegistry("registry", "myregistry.azurecr.io", "myrepo");

        var project = builder.AddProject<Projects.ServiceA>("api");
        using var app = builder.Build();

        // Trigger BeforeStartEvent to propagate RegistryTargetAnnotation
        await app.StartAsync();
        await app.StopAsync();

        var environment = new KubernetesEnvironmentResource("env");

        // Simulate what publish captures: an image reference for the project resource
        environment.CapturedHelmImageReferences.Add(
            new KubernetesEnvironmentResource.CapturedHelmImageReference(
                "parameters", "api", "api_image", project.Resource));

        // Act
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert: values.env.yaml should exist with registry-prefixed image
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.True(File.Exists(deployValuesPath), "values.env.yaml should be created");

        var content = await File.ReadAllTextAsync(deployValuesPath);
        output.WriteLine(content);

        // The image should be prefixed with the registry endpoint and repository
        Assert.Contains("myregistry.azurecr.io/myrepo/api:latest", content);
    }

    [Fact]
    public async Task ResolveAndWriteDeployValuesAsync_NoOverrideFileWhenNoCaptures()
    {
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "env");
        Directory.CreateDirectory(outputPath);

        var environment = new KubernetesEnvironmentResource("env");

        // No captured values, cross-references, or image references
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // No override file should be created
        var deployValuesPath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.False(File.Exists(deployValuesPath));
    }

    [Fact]
    public async Task CrossResourceSecretResolution_ValuesYamlKeysMatchTemplateReferences()
    {
        // Simulates the Redis+server scenario: cache has a password, server references it.
        // The values.yaml keys must match the Helm expression paths in templates.
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        var envBuilder = builder.AddKubernetesEnvironment("env");
        var cachePassword = builder.AddParameter("cache-password", secret: true);

        // Cache container with a password
        var cache = builder.AddContainer("cache", "redis")
            .WithEndpoint(targetPort: 6379, name: "tcp")
            .WithEnvironment("REDIS_PASSWORD", cachePassword);

        // Server container that directly references the cache password
        builder.AddContainer("server", "myserver")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEnvironment("CACHE_PASSWORD", cachePassword);

        using var app = builder.Build();
        var env = envBuilder.Resource;

        await app.RunAsync();

        // Verify: values.yaml should have cache_password key (parameter name) under both cache and server
        var valuesPath = Path.Combine(tempDir.Path, "values.yaml");
        Assert.True(File.Exists(valuesPath), "values.yaml should exist");
        var valuesContent = await File.ReadAllTextAsync(valuesPath);
        output.WriteLine("=== values.yaml ===");
        output.WriteLine(valuesContent);

        // The key should be "cache_password" (from parameter name via ValuesKey), not "CACHE_PASSWORD" or "REDIS_PASSWORD"
        Assert.Contains("cache_password", valuesContent);

        // Verify: CapturedHelmValues should include entries for BOTH resources
        Assert.Contains(env.CapturedHelmValues, c =>
            c.Section == "secrets" &&
            c.ResourceKey == "cache" &&
            c.ValueKey == "cache_password" &&
            c.Parameter.Name == "cache-password");

        Assert.Contains(env.CapturedHelmValues, c =>
            c.Section == "secrets" &&
            c.ResourceKey == "server" &&
            c.ValueKey == "cache_password" &&
            c.Parameter.Name == "cache-password");

        // Verify: server's template should reference {{ .Values.secrets.server.cache_password }}
        var serverSecretsTemplatePath = Path.Combine(tempDir.Path, "templates", "server", "secrets.yaml");
        if (File.Exists(serverSecretsTemplatePath))
        {
            var templateContent = await File.ReadAllTextAsync(serverSecretsTemplatePath);
            output.WriteLine("=== server secrets.yaml template ===");
            output.WriteLine(templateContent);

            // Template should reference the correct path
            Assert.Contains(".Values.secrets.server.cache_password", templateContent);
        }
    }

    [Fact]
    public async Task CrossResourceSecretResolution_ReferenceExpressionWrappedParameter_PreservesValuesKey()
    {
        // Reproduces the real Redis+WithReference scenario where the password env var
        // is provided as ReferenceExpression.Create($"{PasswordParameter}") — a {0} wrapper.
        // Without the fix, the {0} passthrough converts HelmValue to string, losing ValuesKey.
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        var envBuilder = builder.AddKubernetesEnvironment("env");
        var cachePassword = builder.AddParameter("cache-password", secret: true);

        // Cache container with direct password env var
        builder.AddContainer("cache", "redis")
            .WithEndpoint(targetPort: 6379, name: "tcp")
            .WithEnvironment("REDIS_PASSWORD", cachePassword);

        // Server container uses ReferenceExpression wrapping (like real WithReference does)
        // This is the key difference: ReferenceExpression.Create($"{param}") wraps in {0} format
        builder.AddContainer("server", "myserver")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEnvironment(context =>
            {
                // Mimic what Redis's GetConnectionProperties yields: Password wrapped in ReferenceExpression
                context.EnvironmentVariables["CACHE_PASSWORD"] = ReferenceExpression.Create($"{cachePassword.Resource}");
            });

        using var app = builder.Build();
        var env = envBuilder.Resource;

        await app.RunAsync();

        var valuesPath = Path.Combine(tempDir.Path, "values.yaml");
        Assert.True(File.Exists(valuesPath), "values.yaml should exist");
        var valuesContent = await File.ReadAllTextAsync(valuesPath);
        output.WriteLine("=== values.yaml ===");
        output.WriteLine(valuesContent);

        // Critical: the key must be "cache_password" (from parameter name), NOT "CACHE_PASSWORD" (env var name)
        // Without the fix, the {0} passthrough converts HelmValue to string, losing ValuesKey,
        // and the fallback key is the env var name "CACHE_PASSWORD" (case mismatch with template).
        Assert.DoesNotContain("CACHE_PASSWORD", valuesContent);
        Assert.Contains("cache_password", valuesContent);

        // Server's CapturedHelmValue should use the parameter name as the value key
        Assert.Contains(env.CapturedHelmValues, c =>
            c.Section == "secrets" &&
            c.ResourceKey == "server" &&
            c.ValueKey == "cache_password" &&
            c.Parameter.Name == "cache-password");
    }

    [Fact]
    public async Task CrossResourceSecretResolution_OverrideFileResolvesAllPaths()
    {
        // Verifies that Phase 1 and Phase 2 resolution produces a correct override file.
        // Phase 1: Resolves direct ParameterResource values (both cache and server entries)
        // Phase 2: Substitutes Helm expressions in cross-reference templates with Phase 1 values
        using var tempDir = new TestTempDirectory();
        var outputPath = Path.Combine(tempDir.Path, "deploy");
        Directory.CreateDirectory(outputPath);

        var environment = new KubernetesEnvironmentResource("myenv");

        // Create a parameter with a known value callback
        var cachePasswordParam = new ParameterResource("cache-password", _ => "test-password-123", secret: true);

        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue(
                "secrets", "cache", "cache_password", cachePasswordParam));

        environment.CapturedHelmValues.Add(
            new KubernetesEnvironmentResource.CapturedHelmValue(
                "secrets", "server", "cache_password", cachePasswordParam));

        // Simulate cross-reference: server's connection string embeds the cache password
        environment.CapturedHelmCrossReferences.Add(
            new KubernetesEnvironmentResource.CapturedHelmCrossReference(
                "secrets", "server", "connectionstrings__cache",
                "cache-service:6379,password={{ .Values.secrets.server.cache_password }}"));

        environment.CapturedHelmCrossReferences.Add(
            new KubernetesEnvironmentResource.CapturedHelmCrossReference(
                "secrets", "server", "cache_uri",
                "redis://:{{ .Values.secrets.server.cache_password }}@cache-service:6379"));

        // Act
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            outputPath, environment, CancellationToken.None);

        // Assert: override file should exist and have fully resolved values
        var overridePath = Path.Combine(outputPath, HelmDeploymentEngine.GetDeployValuesFileName("myenv"));
        Assert.True(File.Exists(overridePath), "Override file should be created");

        var content = await File.ReadAllTextAsync(overridePath);
        output.WriteLine("=== Override file ===");
        output.WriteLine(content);

        // Phase 1: Both cache and server should have the resolved password
        Assert.Contains("cache_password: test-password-123", content);

        // Phase 2: Cross-references should be fully resolved (no {{ .Values... }} expressions)
        Assert.DoesNotContain("{{ .Values.", content);

        // Phase 2: Connection string should be fully resolved
        Assert.Contains("connectionstrings__cache: cache-service:6379,password=test-password-123", content);
        Assert.Contains("cache_uri: redis://:test-password-123@cache-service:6379", content);
    }

    [Fact]
    public async Task CrossResourceSecretResolution_EndToEnd_PublishAndResolve()
    {
        // Full end-to-end: publish generates correct captures, then resolve produces correct override
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        var envBuilder = builder.AddKubernetesEnvironment("env");
        var cachePassword = builder.AddParameter("cache-password", "e2e-test-pw-42", secret: true);

        // Cache with password
        builder.AddContainer("cache", "redis")
            .WithEndpoint(targetPort: 6379, name: "tcp")
            .WithEnvironment("REDIS_PASSWORD", cachePassword);

        // Server with direct password reference
        builder.AddContainer("server", "myserver")
            .WithHttpEndpoint(targetPort: 8080)
            .WithEnvironment("CACHE_PASSWORD", cachePassword);

        using var app = builder.Build();
        var env = envBuilder.Resource;
        await app.RunAsync();

        // Now simulate deploy-time resolution
        await HelmDeploymentEngine.ResolveAndWriteDeployValuesAsync(
            tempDir.Path, env, CancellationToken.None);

        var overridePath = Path.Combine(tempDir.Path, HelmDeploymentEngine.GetDeployValuesFileName("env"));
        Assert.True(File.Exists(overridePath), "Override file should be created");

        var content = await File.ReadAllTextAsync(overridePath);
        output.WriteLine("=== Override file (E2E) ===");
        output.WriteLine(content);

        // The override file should NOT contain any unresolved Helm expressions
        Assert.DoesNotContain("{{ .Values.", content);

        // Both cache and server should have resolved password values
        Assert.Contains("cache:", content);   // cache resource section
        Assert.Contains("server:", content);   // server resource section
        Assert.Contains("cache_password:", content);

        // Verify the actual password is in the resolved values
        Assert.Contains("e2e-test-pw-42", content);
    }

    [Fact]
    public void AddKubernetesEnvironment_CreatesDashboardByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envBuilder = builder.AddKubernetesEnvironment("env");

        var env = envBuilder.Resource;

        Assert.True(env.DashboardEnabled);
        Assert.NotNull(env.Dashboard);
        Assert.IsType<KubernetesAspireDashboardResource>(env.Dashboard.Resource);
    }

    [Fact]
    public void WithDashboard_Disabled_SetsDashboardEnabledFalse()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envBuilder = builder.AddKubernetesEnvironment("env")
            .WithDashboard(false);

        var env = envBuilder.Resource;

        Assert.False(env.DashboardEnabled);
    }

    [Fact]
    public void WithDashboard_Configure_AllowsCustomization()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envBuilder = builder.AddKubernetesEnvironment("env")
            .WithDashboard(dashboard => dashboard.WithServicePort(9999));

        var env = envBuilder.Resource;

        Assert.True(env.DashboardEnabled);
        Assert.NotNull(env.Dashboard);

        // Verify the host port was configured
        var httpEndpoint = env.Dashboard.Resource.PrimaryEndpoint;
        Assert.NotNull(httpEndpoint);
    }

    [Fact]
    public async Task Dashboard_OtlpConfigured_ForComputeResources()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        var envBuilder = builder.AddKubernetesEnvironment("env");

        // Use a project resource — projects get OtlpExporterAnnotation by default
        builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        await app.RunAsync();

        // Check that values.yaml contains OTLP configuration for the project resource
        var valuesPath = Path.Combine(tempDir.Path, "values.yaml");
        Assert.True(File.Exists(valuesPath));
        var content = await File.ReadAllTextAsync(valuesPath);
        output.WriteLine(content);

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", content);
        Assert.Contains("env-dashboard-service:18889", content);
        Assert.Contains("OTEL_EXPORTER_OTLP_PROTOCOL", content);
        Assert.Contains("OTEL_SERVICE_NAME", content);
    }

    [Fact]
    public async Task Dashboard_Disabled_NoOtlpConfiguration()
    {
        using var tempDir = new TestTempDirectory();

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Publish);
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);

        builder.AddKubernetesEnvironment("env")
            .WithDashboard(false);

        // Use a project resource — projects get OtlpExporterAnnotation by default
        builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        await app.RunAsync();

        // Check that values.yaml does NOT contain OTLP configuration
        var valuesPath = Path.Combine(tempDir.Path, "values.yaml");
        Assert.True(File.Exists(valuesPath));
        var content = await File.ReadAllTextAsync(valuesPath);
        output.WriteLine(content);

        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", content);
        Assert.DoesNotContain("OTEL_SERVICE_NAME", content);
    }

    [Fact]
    public void Dashboard_ResourceHasCorrectEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var envBuilder = builder.AddKubernetesEnvironment("env");

        var dashboard = envBuilder.Resource.Dashboard!.Resource;

        Assert.NotNull(dashboard.PrimaryEndpoint);
        Assert.NotNull(dashboard.OtlpGrpcEndpoint);
        Assert.Equal("http", dashboard.PrimaryEndpoint.EndpointName);
        Assert.Equal("otlp-grpc", dashboard.OtlpGrpcEndpoint.EndpointName);
    }

    [Fact]
    public async Task DestroyHelm_WithState_RunsHelmUninstall()
    {
        using var tempDir = new TestTempDirectory();

        var fakeHelm = new FakeHelmRunner();
        var stateManager = new InMemoryDeploymentStateManager();
        stateManager.SetSection("Helm:env", new JsonObject
        {
            ["ReleaseName"] = "my-release",
            ["Namespace"] = "my-namespace"
        });

        var mockActivityReporter = new TestPipelineActivityReporter(output);
        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Destroy);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        builder.Services.AddSingleton<IHelmRunner>(fakeHelm);
        builder.Services.Configure<PipelineOptions>(o => o.SkipConfirmation = true);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        // Verify helm uninstall was called with saved state values
        Assert.True(fakeHelm.WasUninstallCalled);
        Assert.Contains("my-release", fakeHelm.LastArguments!);
        Assert.Contains("my-namespace", fakeHelm.LastArguments!);
    }

    [Fact]
    public async Task DestroyHelm_WithNoState_ReportsNothingToDestroy()
    {
        using var tempDir = new TestTempDirectory();

        var fakeHelm = new FakeHelmRunner();
        var stateManager = new InMemoryDeploymentStateManager();
        var mockActivityReporter = new TestPipelineActivityReporter(output);

        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Destroy);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        builder.Services.AddSingleton<IHelmRunner>(fakeHelm);
        builder.Services.Configure<PipelineOptions>(o => o.SkipConfirmation = true);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        // Verify helm was NOT called
        Assert.False(fakeHelm.WasUninstallCalled);

        // Verify it reported nothing to destroy
        var completedSteps = mockActivityReporter.CompletedSteps;
        Assert.Contains(completedSteps, s => s.CompletionText.Contains("Nothing to destroy", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FakeHelmRunner : IHelmRunner
    {
        public bool WasUninstallCalled { get; private set; }
        public string? LastArguments { get; private set; }
        public int ExitCode { get; set; }

        public Task<int> RunAsync(
            string arguments,
            string? workingDirectory = null,
            Action<string>? onOutputData = null,
            Action<string>? onErrorData = null,
            CancellationToken cancellationToken = default)
        {
            LastArguments = arguments;
            if (arguments.StartsWith("uninstall", StringComparison.OrdinalIgnoreCase))
            {
                WasUninstallCalled = true;
            }
            return Task.FromResult(ExitCode);
        }
    }

    [Fact]
    public async Task DestroyHelm_WhenUninstallFails_PreservesState()
    {
        using var tempDir = new TestTempDirectory();

        var fakeHelm = new FakeHelmRunner { ExitCode = 1 };
        var stateManager = new InMemoryDeploymentStateManager();
        stateManager.SetSection("Helm:env", new JsonObject
        {
            ["ReleaseName"] = "my-release",
            ["Namespace"] = "my-namespace"
        });

        var mockActivityReporter = new TestPipelineActivityReporter(output);
        var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path,
            step: WellKnownPipelineSteps.Destroy);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IPipelineActivityReporter>(mockActivityReporter);
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        builder.Services.AddSingleton<IHelmRunner>(fakeHelm);
        builder.Services.Configure<PipelineOptions>(o => o.SkipConfirmation = true);

        builder.AddKubernetesEnvironment("env");
        builder.AddContainer("api", "myimage");

        using var app = builder.Build();
        await app.RunAsync();

        // Verify helm uninstall was attempted
        Assert.True(fakeHelm.WasUninstallCalled);

        // Verify state was NOT deleted (preserved for retry)
        var stateSection = await stateManager.AcquireSectionAsync("Helm:env");
        Assert.Equal("my-release", stateSection.Data["ReleaseName"]?.ToString());
    }
}
