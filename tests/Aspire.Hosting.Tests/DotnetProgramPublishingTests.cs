// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests;

public class DotnetProgramPublishingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ProjectResourceIsConfiguredForDotnetProgramPublishing()
    {
        var resource = new ProjectResource("project");

        Assert.IsAssignableFrom<IDotnetProgramResource>(resource);
        Assert.True(resource.SupportsDotnetProgramPublishing());
        Assert.Single(resource.Annotations.OfType<DotnetProgramPublishingAnnotation>());
        Assert.Single(resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Single(resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Single(resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
    }

    [Fact]
    public void WithDotnetProgramPublishingRequiresProjectMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new TestDotnetProgramResource("program"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.WithDotnetProgramPublishing();
        });

        Assert.Equal(
            $"Resource 'program' does not carry an {nameof(IProjectMetadata)} annotation.",
            exception.Message);
        Assert.False(resource.Resource.SupportsDotnetProgramPublishing());
    }

    [Fact]
    public void WithDotnetProgramPublishingRequiresComputeResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new NonComputeDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.WithDotnetProgramPublishing();
        });

        Assert.Equal(
            $"Resource 'program' must implement {nameof(IComputeResource)} to use .NET SDK publishing.",
            exception.Message);
        Assert.False(resource.Resource.SupportsDotnetProgramPublishing());
    }

    [Fact]
    public void WithDotnetProgramPublishingIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"));

        resource.WithDotnetProgramPublishing();
        resource.WithDotnetProgramPublishing();

        Assert.True(resource.Resource.SupportsDotnetProgramPublishing());
        Assert.Single(resource.Resource.Annotations.OfType<DotnetProgramPublishingAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
    }

    [Fact]
    public void GetProjectMetadataValidatesDuplicateMetadata()
    {
        var resource = new TestDotnetProgramResource("program");
        resource.Annotations.Add(new TestProjectMetadata("first.csproj"));
        resource.Annotations.Add(new TestProjectMetadata("second.csproj"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            resource.GetProjectMetadata();
        });

        Assert.Contains("carries more than one IProjectMetadata annotation", exception.Message);
    }

    [Fact]
    public async Task ConfiguredProgramUsesProjectManifestWithoutEvaluatingLaunchToolArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var launchToolCallbackInvoked = false;
        Action<CommandLineArgsCallbackContext> launchToolCallback = context =>
        {
            launchToolCallbackInvoked = true;
            context.Args.Add("run");
            context.Args.Add("--project");
            context.Args.Add(projectPath);
        };
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata(projectPath))
            .WithDotnetProgramPublishing()
            .WithLaunchToolArgs(launchToolCallback)
            .WithArgs("application-argument");

        var manifest = await ManifestUtils.GetManifest(
            resource.Resource,
            workspace.WorkspaceRoot.FullName);

        Assert.False(launchToolCallbackInvoked);
        Assert.Equal("project.v0", manifest["type"]?.GetValue<string>());
        Assert.Equal("program.csproj", manifest["path"]?.GetValue<string>());
        Assert.Equal(
            ["application-argument"],
            manifest["args"]?.AsArray().Select(static value => value!.GetValue<string>()));
    }

    [Fact]
    public void ConfiguredProgramParticipatesInComputeAndBuildSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"))
            .WithDotnetProgramPublishing();
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Contains(resource.Resource, model.GetComputeResources());
        Assert.Contains(resource.Resource, model.GetBuildResources());
        Assert.True(resource.Resource.RequiresImageBuild());
    }

    [Fact]
    public async Task PrebuiltProgramParticipatesInComputeWithoutBuildOrPushSteps()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var processRunner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var runtime = new FakeContainerRuntime
        {
            ResolveAsyncCallback = _ => throw new InvalidOperationException("Prebuilt images must not resolve a container runtime.")
        };
        builder.Services.AddFakeContainerRuntime(runtime);
        var resource = builder.AddResource(new TestDotnetProgramResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"))
            .WithDotnetProgramPublishing()
            .WithAnnotation(new ContainerImageAnnotation
            {
                Registry = "example.com",
                Image = "program",
                Tag = "v1"
            });
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Contains(resource.Resource, model.GetComputeResources());
        Assert.DoesNotContain(resource.Resource, model.GetBuildResources());
        Assert.DoesNotContain(resource.Resource, model.GetBuildAndPushResources());
        Assert.False(resource.Resource.RequiresImageBuild());
        Assert.True(resource.Resource.SupportsDotnetProgramPublishing());

        var pipelineContext = new PipelineContext(
            model,
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = resource.Resource
        });

        Assert.Empty(steps);

        var imageManager = app.Services.GetRequiredService<IResourceContainerImageManager>();
        await imageManager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);
        await imageManager.BuildImagesAsync([resource.Resource], TestContext.Current.CancellationToken);
        Assert.Empty(processRunner.ProcessSpecs);
        Assert.Equal(0, runtime.ResolveAsyncCallCount);
    }

    [Fact]
    public void DotnetProgramReplicasProduceDistinctDcpInstances()
    {
        var resource = new TestDotnetProgramResource("program");
        resource.Annotations.Add(new ReplicaAnnotation(3));
        var nameGenerator = new DcpNameGenerator(
            new ConfigurationBuilder().Build(),
            Options.Create(new DcpOptions()));

        nameGenerator.EnsureDcpInstancesPopulated(resource);

        Assert.True(resource.TryGetInstances(out var instances));
        Assert.Equal(3, instances.Length);
        Assert.Equal([0, 1, 2], instances.Select(static instance => instance.Index));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ArchiveBuildPipelineOnlyRequiresRuntimeForContainerFiles(bool legacyProject, bool containerFiles)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var processRunner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var runtime = new FakeContainerRuntime(isRunning: false, name: "Docker");
        builder.Services.AddFakeContainerRuntime(runtime);
        builder.Services.AddSingleton<IInteractionService>(new TestInteractionService { IsAvailable = false });

        var callbackCount = 0;
        IResourceBuilder<IDotnetProgramResource> resource = legacyProject
            ? builder.AddResource(new ProjectResource("program"))
            : builder.AddResource(new TestDotnetProgramResource("program"));
        resource.WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "program.csproj")))
            .WithDotnetProgramPublishing()
            .WithAnnotation(new ContainerBuildOptionsCallbackAnnotation(context =>
            {
                callbackCount++;
                context.Destination = ContainerImageDestination.Archive;
                context.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "program.tar");
            }));
        if (containerFiles)
        {
            var source = builder.AddContainer("assets", "assets-image")
                .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/assets" });
            resource.WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = source.Resource,
                DestinationPath = "wwwroot"
            });
        }

        using var app = builder.Build();
        if (containerFiles)
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => ExecuteBuildPipelineAsync(app, "build-program")).DefaultTimeout();
            Assert.Equal("Docker is not running. Start Docker and try again.", exception.Message);
            Assert.True(runtime.WasHealthCheckCalled);
            Assert.Empty(processRunner.ProcessSpecs);
        }
        else
        {
            await ExecuteBuildPipelineAsync(app, "build-program").DefaultTimeout();
            Assert.False(runtime.WasHealthCheckCalled);
            Assert.Equal("publish", Assert.Single(processRunner.ProcessSpecs).ArgumentList![0]);
        }

        Assert.Equal(1, callbackCount);
        Assert.False(runtime.WasBuildImageCalled);
    }

    [Theory]
    [InlineData(WellKnownPipelineSteps.Build, 2)]
    [InlineData(WellKnownPipelineSteps.CheckContainerRuntime, 0)]
    public async Task PipelineRecoversRuntimeForRequiredBuildsAndExplicitChecks(string stepName, int publishCount)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var processRunner = new TestProcessRunner();
        builder.Services.AddSingleton<IProcessRunner>(processRunner);
        var running = 0;
        var runtime = new FakeContainerRuntime(name: "Docker")
        {
            CheckIfRunningAsyncCallback = _ => Task.FromResult(Volatile.Read(ref running) != 0)
        };
        builder.Services.AddFakeContainerRuntime(runtime);
        var interactions = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(interactions);

        builder.AddResource(new ProjectResource("local"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "local.csproj")));
        builder.AddResource(new ProjectResource("archive"))
            .WithAnnotation(new TestProjectMetadata(Path.Combine(workspace.WorkspaceRoot.FullName, "archive.csproj")))
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.OutputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "archive.tar");
            });
        using var app = builder.Build();
        var build = ExecuteBuildPipelineAsync(app, stepName);
        var interaction = await interactions.Interactions.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().DefaultTimeout();

        Interlocked.Exchange(ref running, 1);
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(true));
        await build.DefaultTimeout();

        Assert.Equal(2, runtime.CheckIfRunningCallCount);
        Assert.Equal(publishCount, processRunner.ProcessSpecs.Count);
        Assert.All(processRunner.ProcessSpecs, spec => Assert.Equal("publish", spec.ArgumentList![0]));
        Assert.False(interactions.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task DirectImageBuildRetainsNonInteractiveRuntimeFailure()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var runtime = new FakeContainerRuntime(isRunning: false, name: "Docker");
        builder.Services.AddFakeContainerRuntime(runtime);
        var interactions = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(interactions);
        var resource = builder.AddResource(new ProjectResource("program"))
            .WithAnnotation(new TestProjectMetadata("program.csproj"));
        using var app = builder.Build();
        var manager = app.Services.GetRequiredService<IResourceContainerImageManager>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken)).DefaultTimeout();

        Assert.Equal("Container runtime 'Docker' is not running or is unhealthy.", exception.Message);
        Assert.False(interactions.Interactions.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData("artifacts", false, false)]
    [InlineData("artifacts.v1", false, true)]
    [InlineData("image.custom", false, true)]
    [InlineData("image.tar", false, true)]
    [InlineData("image.tar.gz", false, true)]
    [InlineData("image.tgz", false, true)]
    [InlineData("artifacts.v1", true, false)]
    [InlineData("image.tar", true, false)]
    public void ArchiveOutputPathPreservesSdkFilenameConventions(string name, bool directoryIntent, bool explicitArchive)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, name);
        if (directoryIntent)
        {
            path += Path.DirectorySeparatorChar;
        }

        Assert.Equal(explicitArchive, DotnetProgramPublishing.IsExplicitArchiveOutputPath(path));
    }

    [Theory]
    [InlineData("artifacts.v1", true)]
    [InlineData("image.tar", true)]
    [InlineData("archive", false)]
    [InlineData("image.custom", false)]
    public void ArchiveOutputPathPreservesExistingFileAndDirectoryIntent(string name, bool directory)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, name);
        if (directory)
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            File.WriteAllText(path, "archive");
        }

        Assert.Equal(!directory, DotnetProgramPublishing.IsExplicitArchiveOutputPath(path));
    }

    private static Task ExecuteBuildPipelineAsync(DistributedApplication app, string stepName)
    {
        var pipeline = Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>());
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        return pipeline.ExecuteStepSequentiallyAsync(stepName, context);
    }

    private sealed class TestDotnetProgramResource(string name) :
        Resource(name),
        IDotnetProgramResource,
        IComputeResource,
        IResourceWithArgs;

    private sealed class NonComputeDotnetProgramResource(string name) : Resource(name), IDotnetProgramResource;

    private sealed class TestProjectMetadata(string projectPath) : IProjectMetadata
    {
        public string ProjectPath { get; } = projectPath;
    }
}
