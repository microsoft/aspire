// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Kubernetes.Tests;

public class HelmChartPublishingResultTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ResultIsAvailableToDependentStepAndPreservesDeferredSources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var environment = builder.AddKubernetesEnvironment("env").WithDashboard(false);
        var parameter = builder.AddParameter("ordinary");
        var secret = builder.AddParameter("password", secret: true);
        var provider = new TestValueProvider("must-not-be-published", "{outputs.deferred}");
        var api = builder.AddContainer("api-service", "nginx")
            .WithEnvironment("ORDINARY", parameter)
            .WithEnvironment("SECRET", secret)
            .WithEnvironment("DEFERRED", provider)
            .WithEnvironment("COMPOSITE", $"prefix-{parameter}-{secret}-suffix");
        var project = builder.AddDotnetProject("worker", "worker.csproj", options => options.ExcludeLaunchProfile = true);

        Assert.Null(environment.Resource.HelmChartPublishingResult);
        HelmChartPublishingResult? observed = null;
        builder.Pipeline.AddStep("consume-chart", _ =>
        {
            observed = Assert.IsType<HelmChartPublishingResult>(environment.Resource.HelmChartPublishingResult);
            Assert.True(File.Exists(Path.Combine(observed.OutputPath, "Chart.yaml")));
            Assert.True(File.Exists(Path.Combine(observed.OutputPath, "values.yaml")));
            Assert.True(File.Exists(Path.Combine(observed.OutputPath, "templates", api.Resource.Name, "deployment.yaml")));
            return Task.CompletedTask;
        }, dependsOn: $"publish-{environment.Resource.Name}", requiredBy: WellKnownPipelineSteps.Publish);

        using var app = builder.Build();
        await app.RunAsync();

        var result = Assert.IsType<HelmChartPublishingResult>(observed);
        Assert.Same(result, environment.Resource.HelmChartPublishingResult);
        Assert.Equal(workspace.Path, result.OutputPath);
        Assert.Contains(result.Parameters, mapping => ReferenceEquals(mapping.Value, parameter.Resource));
        Assert.Contains(result.Parameters, mapping => ReferenceEquals(mapping.Value, secret.Resource));
        Assert.Same(provider, Assert.Single(result.ValueProviders).Value);
        Assert.Equal(0, provider.GetValueCallCount);
        Assert.Same(project.Resource, Assert.Single(result.Images).Value);
        Assert.NotEmpty(result.CrossReferences);

        Assert.Equal(environment.Resource.CapturedHelmValues.Select(value =>
            (value.Section, value.ResourceKey, value.ValueKey, value.Parameter)),
            result.Parameters.Select(value => (value.Section, value.ResourceKey, value.ValueKey, value.Value)));
        Assert.Equal(environment.Resource.CapturedHelmValueProviders.Select(value =>
            (value.Section, value.ResourceKey, value.ValueKey, value.ValueProvider)),
            result.ValueProviders.Select(value => (value.Section, value.ResourceKey, value.ValueKey, value.Value)));
        Assert.Equal(environment.Resource.CapturedHelmImageReferences.Select(value =>
            (value.Section, value.ResourceKey, value.ValueKey, value.Resource)),
            result.Images.Select(value => (value.Section, value.ResourceKey, value.ValueKey, value.Value)));
        Assert.Equal(environment.Resource.CapturedHelmCrossReferences.Select(value =>
            (value.Section, value.ResourceKey, value.ValueKey, value.TemplateValue)),
            result.CrossReferences.Select(value => (value.Section, value.ResourceKey, value.ValueKey, value.Value)));

        await Verify(new
        {
            Parameters = result.Parameters.Select(value => new { value.Section, value.ResourceKey, value.ValueKey, value.Value.Name, value.Value.Secret }),
            ValueProviders = result.ValueProviders.Select(value => new { value.Section, value.ResourceKey, value.ValueKey }),
            Images = result.Images.Select(value => new { value.Section, value.ResourceKey, value.ValueKey, value.Value.Name }),
            result.CrossReferences
        }).AppendContentAsFile(await File.ReadAllTextAsync(Path.Combine(result.OutputPath, "values.yaml")), "yaml");
    }

    [Fact]
    public async Task RepeatedPublishingReplacesResultWithoutMutatingPreviousSnapshot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var environment = builder.AddKubernetesEnvironment("env").WithDashboard(false);
        var parameter = builder.AddParameter("parameter", secret: true);
        var provider = new TestValueProvider("deferred");
        builder.AddDotnetProject("api", "api.csproj", options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("PARAMETER", parameter)
            .WithEnvironment("DEFERRED", provider)
            .WithEnvironment("COMPOSITE", $"prefix-{parameter}");
        using var app = builder.Build();
        await app.RunAsync();

        var first = Assert.IsType<HelmChartPublishingResult>(environment.Resource.HelmChartPublishingResult);
        var parameters = first.Parameters.ToArray();
        var providers = first.ValueProviders.ToArray();
        var images = first.Images.ToArray();
        var templates = first.CrossReferences.ToArray();
        Assert.Throws<NotSupportedException>(() => ((IList<HelmValueMapping<ParameterResource>>)first.Parameters).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HelmValueMapping<IValueProvider>>)first.ValueProviders).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HelmValueMapping<IResource>>)first.Images).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<HelmValueMapping<string>>)first.CrossReferences).Clear());

        var secondOutput = Path.Combine(workspace.Path, "second");
        var context = new KubernetesPublishingContext(builder.ExecutionContext, secondOutput, NullLogger.Instance, environment.Resource);
        await context.WriteModelAsync(app.Services.GetRequiredService<DistributedApplicationModel>(), environment.Resource);

        var second = Assert.IsType<HelmChartPublishingResult>(environment.Resource.HelmChartPublishingResult);
        Assert.NotSame(first, second);
        Assert.Equal(secondOutput, second.OutputPath);
        Assert.Equal(first.Parameters.Count, second.Parameters.Count);
        Assert.Equal(first.ValueProviders.Count, second.ValueProviders.Count);
        Assert.Equal(first.Images.Count, second.Images.Count);
        Assert.Equal(first.CrossReferences.Count, second.CrossReferences.Count);

        environment.Resource.CapturedHelmValues.Clear();
        environment.Resource.CapturedHelmValueProviders.Clear();
        environment.Resource.CapturedHelmImageReferences.Clear();
        environment.Resource.CapturedHelmCrossReferences.Clear();
        Assert.Equal(parameters, first.Parameters);
        Assert.Equal(providers, first.ValueProviders);
        Assert.Equal(images, first.Images);
        Assert.Equal(templates, first.CrossReferences);
        Assert.Equal(workspace.Path, first.OutputPath);
    }

    [Fact]
    public async Task FailedPublishingClearsPreviousResult()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var environment = builder.AddKubernetesEnvironment("env").WithDashboard(false);
        builder.AddContainer("api", "nginx");
        using var app = builder.Build();
        await app.RunAsync();
        Assert.NotNull(environment.Resource.HelmChartPublishingResult);

        var outputFile = Path.Combine(workspace.Path, "not-a-directory");
        await File.WriteAllTextAsync(outputFile, "existing file");
        var context = new KubernetesPublishingContext(builder.ExecutionContext, outputFile, NullLogger.Instance, environment.Resource);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            context.WriteModelAsync(app.Services.GetRequiredService<DistributedApplicationModel>(), environment.Resource));

        Assert.Null(environment.Resource.HelmChartPublishingResult);
    }

    [Fact]
    public async Task RunModeDoesNotProduceResult()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var environment = builder.AddKubernetesEnvironment("env");
        using var app = builder.Build();
        var context = new KubernetesPublishingContext(builder.ExecutionContext, "unused", NullLogger.Instance, environment.Resource);

        await context.WriteModelAsync(app.Services.GetRequiredService<DistributedApplicationModel>(), environment.Resource);

        Assert.Null(environment.Resource.HelmChartPublishingResult);
    }
}
