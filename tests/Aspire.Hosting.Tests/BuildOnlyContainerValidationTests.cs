// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "6")]
public class BuildOnlyContainerValidationTests
{
    [Fact]
    public async Task PublishPrereq_WithUnconsumedBuildOnlyContainer_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PublishPrereq);

        AddBuildOnlyContainer(builder, "frontend");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app)).DefaultTimeout();

        Assert.Contains("'frontend'", ex.Message);
        Assert.Contains("PublishWithContainerFiles", ex.Message);
        Assert.Contains("DisableBuildOnlyContainerValidation", ex.Message);
    }

    [Fact]
    public async Task DeployPrereq_WithUnconsumedBuildOnlyContainer_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.DeployPrereq);

        AddBuildOnlyContainer(builder, "frontend");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app)).DefaultTimeout();

        Assert.Contains("'frontend'", ex.Message);
    }

    [Fact]
    public async Task PublishPrereq_WithConsumedBuildOnlyContainer_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PublishPrereq);

        var source = AddBuildOnlyContainer(builder, "frontend");
        builder.AddResource(new TestContainerFilesDestinationResource("api"))
            .PublishWithContainerFiles(source, "/app/wwwroot");

        using var app = builder.Build();

        await ExecutePipelineAsync(app).DefaultTimeout();
    }

    [Fact]
    public async Task PublishPrereq_WithValidationDisabled_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.PublishPrereq);

        PipelineStep? validationStep = null;
        builder.DisableBuildOnlyContainerValidation();
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            validationStep = context.Steps.Single(step => step.Name == DistributedApplicationPipeline.ValidateBuildOnlyContainerReferencesStepName);
            return Task.CompletedTask;
        });
        AddBuildOnlyContainer(builder, "frontend");

        using var app = builder.Build();

        await ExecutePipelineAsync(app).DefaultTimeout();

        Assert.NotNull(validationStep);
        Assert.Empty(validationStep.RequiredBySteps);
    }

    private static IResourceBuilder<TestContainerFilesSourceResource> AddBuildOnlyContainer(
        IDistributedApplicationBuilder builder,
        string name)
    {
        return builder.AddResource(new TestContainerFilesSourceResource(name))
            .WithAnnotation(new DockerfileBuildAnnotation("context", "Dockerfile", null) { HasEntrypoint = false })
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });
    }

    private static Task ExecutePipelineAsync(DistributedApplication app)
    {
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<BuildOnlyContainerValidationTests>>(),
            CancellationToken.None);

        return pipeline.ExecuteAsync(context);
    }

    private sealed class TestContainerFilesSourceResource(string name) : ContainerResource(name), IResourceWithContainerFiles
    {
    }

    private sealed class TestContainerFilesDestinationResource(string name) : ContainerResource(name), IContainerFilesDestinationResource
    {
    }
}
