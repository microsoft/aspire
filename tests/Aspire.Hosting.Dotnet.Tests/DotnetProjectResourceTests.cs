// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPERSISTENCE001
#pragma warning disable ASPIREPIPELINES001

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectResourceTests(ITestOutputHelper outputHelper)
{
    private static readonly string[] s_unsupportedPublishMessageFragments =
    [
        "is not supported",
        "C# AppHost",
        "AddProject<TProject>(...)",
        "AddCSharpApp(...)",
        "addCSharpApp(...)",
        "PublishAsDockerFile(...)",
        "publishAsDockerFile(...)",
        "ExcludeFromManifest()",
        "excludeFromManifest()",
        "TypeScript"
    ];

    [Fact]
    public async Task AddDotnetProject_ProjectFile_ProducesDotnetRunProjectArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // run --project <path> [--configuration <cfg>] --no-launch-profile
        // (--configuration is only present when the app host assembly declares a build configuration)
        Assert.Equal("run", args[0]);
        Assert.Equal("--project", args[1]);
        Assert.Equal(projectPath, args[2]);
        Assert.Equal("--no-launch-profile", args[^1]);
    }

    [Fact]
    public async Task AddDotnetProject_FileBasedApp_ProducesDotnetRunFileArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var appPath = Path.Combine(builder.AppHostDirectory, "service.cs");
        var app = builder.AddDotnetProject("svc", appPath, o => o.ExcludeLaunchProfile = true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // run --file <path> --no-cache [--configuration <cfg>] --no-launch-profile
        Assert.Equal("run", args[0]);
        Assert.Equal("--file", args[1]);
        Assert.Equal(appPath, args[2]);
        Assert.Equal("--no-cache", args[3]);
        Assert.Equal("--no-launch-profile", args[^1]);
    }

    [Fact]
    public void AddDotnetProject_UsesDotnetCommandAndProjectDirectoryAsWorkingDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        Assert.Equal("dotnet", app.Resource.Command);
        Assert.Equal(Path.GetDirectoryName(projectPath), app.Resource.WorkingDirectory);
    }

    [Fact]
    public void AddDotnetProject_ResourceSupportsServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddDotnetProject("svc", "MyService.csproj", o => o.ExcludeLaunchProfile = true);

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
        Assert.IsAssignableFrom<ExecutableResource>(app.Resource);
    }

    [Fact]
    public void AddDotnetProject_AddsProjectMetadataAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<IProjectMetadata>(out var metadata));
        Assert.Equal(projectPath, metadata.ProjectPath);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("directory")]
    [InlineData("file")]
    public async Task AddDotnetProject_InPublishMode_ManifestPublishingThrows(string appKind)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = appKind switch
        {
            "project" => CreateFile(workspace.Path, "MyService.csproj"),
            "directory" => CreateProjectDirectory(workspace.Path),
            "file" => CreateFile(workspace.Path, "service.cs"),
            _ => throw new ArgumentOutOfRangeException(nameof(appKind), appKind, null)
        };

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddDotnetProject("svc", appPath, o => o.ExcludeLaunchProfile = true);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ManifestUtils.GetManifest(app.Resource, workspace.Path));

        AssertUnsupportedPublishMessage(exception, "Resource 'svc' is a DotnetProjectResource.");
    }

    [Theory]
    [InlineData(WellKnownPipelineSteps.Publish)]
    [InlineData(WellKnownPipelineSteps.Deploy)]
    public async Task AddDotnetProject_InPublishMode_PipelineThrows(string step)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateFile(workspace.Path, "MyService.csproj");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app));

        AssertUnsupportedPublishMessage(exception, "Resource 'svc' is a DotnetProjectResource.");
    }

    [Fact]
    public async Task AddDotnetProject_InPublishMode_ManifestPipelineFailsBeforeCreatingOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            workspace.Path,
            step: "publish-manifest");
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app));

        Assert.False(File.Exists(Path.Combine(workspace.Path, "aspire-manifest.json")));
    }

    [Theory]
    [InlineData(WellKnownPipelineSteps.Publish)]
    [InlineData(WellKnownPipelineSteps.Deploy)]
    public async Task AddDotnetProject_InPublishMode_BlocksSiblingPublishAndDeployWork(string step)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        // This matches publish steps such as Docker Compose's:
        //   test-{step}-work --RequiredBy--> publish/deploy
        // The sibling intentionally has no dependency on publish-prereq/deploy-prereq.
        var workExecuted = false;
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = $"test-{step}-work",
            Action = _ =>
            {
                workExecuted = true;
                return Task.CompletedTask;
            },
            RequiredBySteps = [step]
        });

        using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecutePipelineAsync(app));

        Assert.False(workExecuted);
    }

    [Theory]
    [InlineData(WellKnownPipelineSteps.Build)]
    [InlineData(WellKnownPipelineSteps.Push)]
    public async Task AddDotnetProject_InDeployMode_BlocksWorkWiredByLaterConfigurationCallback(string workRootStep)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            step: WellKnownPipelineSteps.Deploy);
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        var workStepName = $"test-{workRootStep}-work";
        var deployStepName = $"test-{workRootStep}-deploy";
        var workExecuted = 0;
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = workStepName,
            Action = _ =>
            {
                Interlocked.Exchange(ref workExecuted, 1);
                return Task.CompletedTask;
            },
            RequiredBySteps = [workRootStep]
        });
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = deployStepName,
            Action = _ => Task.CompletedTask,
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        });

        // The resource is deliberately added after the .NET project so this callback runs after the validation
        // callback, matching compute environments that attach build and push work to deploy late.
        builder.AddContainer("late-wiring", "image")
            .WithPipelineConfiguration(context =>
                context.Steps.Single(step => step.Name == deployStepName).DependsOn(workStepName));

        using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecutePipelineAsync(app));

        Assert.Equal(0, Volatile.Read(ref workExecuted));
    }

    [Fact]
    public async Task AddDotnetProject_InBuildMode_DoesNotRunPublishValidation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            step: WellKnownPipelineSteps.Build);
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        // This matches build work registered by buildable project and container resources:
        //   build -> test-build-work -> build-prereq -> process-parameters
        var workExecuted = false;
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "test-build-work",
            Action = _ =>
            {
                workExecuted = true;
                return Task.CompletedTask;
            },
            DependsOnSteps = [WellKnownPipelineSteps.BuildPrereq],
            RequiredBySteps = [WellKnownPipelineSteps.Build]
        });

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        Assert.True(workExecuted);
    }

    [Fact]
    public async Task AddDotnetProject_InPushMode_DoesNotRunPublishValidation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            step: WellKnownPipelineSteps.Push);
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        var workExecuted = false;
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "test-push-work",
            Action = _ =>
            {
                workExecuted = true;
                return Task.CompletedTask;
            },
            DependsOnSteps = [WellKnownPipelineSteps.PushPrereq],
            RequiredBySteps = [WellKnownPipelineSteps.Push]
        });

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        Assert.True(workExecuted);
    }

    [Fact]
    public async Task AddDotnetProject_InRunMode_DoesNotGateSharedBeforeStartWork()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Services.Configure<PipelineOptions>(options => options.Step = WellKnownPipelineSteps.BeforeStart);
        builder.AddDotnetProject("svc", CreateFile(workspace.Path, "MyService.csproj"), o => o.ExcludeLaunchProfile = true);

        // This matches the Docker Compose dependency shape:
        //   deploy -> test-deploy -> test-prepare -> validate-compute-environments <- before-start
        // The shared validation step must remain usable by Run mode without invoking publish validation.
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "test-prepare",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = [WellKnownPipelineSteps.ValidateComputeEnvironments]
        });
        builder.Pipeline.AddStep(new PipelineStep
        {
            Name = "test-deploy",
            Action = _ => Task.CompletedTask,
            DependsOnSteps = ["test-prepare"],
            RequiredBySteps = [WellKnownPipelineSteps.Deploy]
        });

        using var app = builder.Build();
        await ExecutePipelineAsync(app);
    }

    [Fact]
    public async Task DirectlyConstructedDotnetProjectResource_InPublishMode_PipelineThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddResource(new DotnetProjectResource("svc", workspace.Path));

        using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app));
    }

    [Fact]
    public async Task MultipleDotnetProjectResources_WithExplicitOptIns_InPublishMode_PipelineThrows()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddDotnetProject("api", CreateFile(workspace.Path, "Api.csproj"), o => o.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", CreateFile(workspace.Path, "Worker.csproj"), o => o.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("excluded", CreateFile(workspace.Path, "Excluded.csproj"), o => o.ExcludeLaunchProfile = true)
            .ExcludeFromManifest();
        builder.AddDotnetProject("containerized", CreateFile(workspace.Path, "Containerized.csproj"), o => o.ExcludeLaunchProfile = true)
            .PublishAsDockerFile();

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecutePipelineAsync(app));

        AssertUnsupportedPublishMessage(
            exception,
            "Resources 'api', 'worker' are DotnetProjectResource instances.");
    }

    [Fact]
    public async Task AddDotnetProject_ExcludedFromManifest_DoesNotFailPublishing()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateFile(workspace.Path, "MyService.csproj");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
            .ExcludeFromManifest();

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        var manifest = await ManifestUtils.GetManifestOrNull(resource.Resource, workspace.Path);
        Assert.Null(manifest);
    }

    [Fact]
    public async Task AddDotnetProject_WithManifestPublishingCallback_ProducesCustomManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateFile(workspace.Path, "MyService.csproj");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
            .WithManifestPublishingCallback(context => context.Writer.WriteString("type", "custom.v0"));

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        var manifest = await ManifestUtils.GetManifest(resource.Resource, workspace.Path);
        Assert.Equal("""{"type":"custom.v0"}""", manifest.ToJsonString());
    }

    [Fact]
    public async Task AddDotnetProject_PublishAsDockerFile_ProducesContainerManifest()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateFile(workspace.Path, "MyService.csproj");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM scratch");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
            .PublishAsDockerFile();

        using var app = builder.Build();
        await ExecutePipelineAsync(app);

        var manifest = await ManifestUtils.GetManifest(resource.Resource, workspace.Path);
        var expected =
            """
            {
              "type": "container.v1",
              "build": {
                "context": ".",
                "dockerfile": "Dockerfile"
              },
              "env": {
                "OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY": "in_memory"
              }
            }
            """;

        Assert.Equal(expected, manifest.ToString(), ignoreLineEndingDifferences: true, ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void AddDotnetProject_DirectoryContainingSingleProjectFile_ResolvesToThatProjectFile()
    {
        // DotnetProjectMetadata defers path resolution to ProjectPathResolver, which resolves a directory
        // containing exactly one .csproj to that project file. Verify AddDotnetProject preserves that
        // contract end-to-end (it used to be exercised only through the core AddProject<T> path).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDir = Directory.CreateDirectory(Path.Combine(workspace.Path, "MyService"));
        var projectPath = Path.Combine(projectDir.FullName, "MyService.csproj");
        File.WriteAllText(projectPath, "<Project />");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddDotnetProject("svc", projectDir.FullName, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<IProjectMetadata>(out var metadata));
        Assert.Equal(projectPath, metadata.ProjectPath);
    }

    [Fact]
    public void AddDotnetProject_AmbiguousDirectory_PassesPathThroughUnchanged()
    {
        // When a directory contains zero or multiple .csproj files, ProjectPathResolver deliberately passes
        // the directory path through unchanged rather than throwing, so the failure surfaces later as a
        // resource start error naming the resource.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDir = Directory.CreateDirectory(Path.Combine(workspace.Path, "AmbiguousService"));
        File.WriteAllText(Path.Combine(projectDir.FullName, "First.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(projectDir.FullName, "Second.csproj"), "<Project />");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddDotnetProject("svc", projectDir.FullName, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<IProjectMetadata>(out var metadata));
        Assert.Equal(projectDir.FullName, metadata.ProjectPath);
    }

    [Fact]
    public void AddDotnetProject_AddsSupportsDebuggingAnnotationInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddDotnetProject("appName", "app-path", options => { options.ExcludeLaunchProfile = true; });

        var annotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("project", annotation.LaunchConfigurationType);
    }

    [Fact]
    public async Task AddDotnetProject_MaterializesEndpointsFromLaunchProfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDir = Directory.CreateDirectory(Path.Combine(workspace.Path, "MyService"));
        var projectPath = Path.Combine(projectDir.FullName, "MyService.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />");

        var propertiesDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(propertiesDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5111"
                }
              }
            }
            """);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddDotnetProject("svc", projectPath);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.UriScheme);
        Assert.Equal(5111, endpoint.Port);
    }

    [Fact]
    public void AddLifeCycleCommands_DotnetProjectResource_RestartHasDetailedProjectDescription()
    {
        // A DotnetProjectResource is a .NET app launched via the SDK, so it should receive the same
        // detailed "rebuild is required" restart description that ProjectResource gets. The marker for
        // that is the project-defaults annotation applied by AddDotnetProject.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var resource = builder.AddDotnetProject("testapp", projectPath, o => o.ExcludeLaunchProfile = true).Resource;
        resource.AddLifeCycleCommands();

        var restartCommand = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
    }

    [Fact]
    public void AddLifeCycleCommands_DirectlyConstructedDotnetProjectResource_RestartHasDetailedProjectDescription()
    {
        // The type has a public constructor, so it can be added with AddResource instead of
        // AddDotnetProject. It is still a .NET app launched via the SDK, so the constructor carries the
        // project-defaults annotation and the resource gets the same treatment as ProjectResource.
        var resource = new DotnetProjectResource("testapp", AppContext.BaseDirectory);
        resource.AddLifeCycleCommands();

        var restartCommand = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
        Assert.Contains(resource.Annotations.OfType<ResourceCommandAnnotation>(), a => a.Name == KnownResourceCommands.RebuildCommand);
    }

    [Fact]
    public async Task AddDotnetProject_DebugAnnotator_ProducesProjectLaunchConfiguration()
    {
        // The "project" SupportsDebuggingAnnotation must produce a ProjectLaunchConfiguration carrying the
        // project path so the IDE (and DCP) can launch/debug it exactly like AddProject. The producer also
        // resolves the launch profile selection, so an out-of-assembly integration gets the complete
        // configuration without doing any of that work itself.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));
        Assert.Equal(KnownLaunchConfigurationTypes.Project, supportsDebugging.LaunchConfigurationType);

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            app.Resource,
            ExecutableLaunchMode.Debug);
        var launchConfig = Assert.IsType<ProjectLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(callbackContext));
        Assert.Equal(KnownLaunchConfigurationTypes.Project, launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
        Assert.Equal(projectPath, launchConfig.ProjectPath);
        Assert.True(launchConfig.DisableLaunchProfile);
        Assert.Equal(string.Empty, launchConfig.LaunchProfile);
    }

    [Fact]
    public async Task AddDotnetProject_LaunchConfiguration_ResolvesEffectiveLaunchProfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDir = Directory.CreateDirectory(Path.Combine(workspace.Path, "MyService"));
        var projectPath = Path.Combine(projectDir.FullName, "MyService.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />");

        var propertiesDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(propertiesDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5111"
                }
              }
            }
            """);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var app = builder.AddDotnetProject("svc", projectPath);

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            app.Resource,
            ExecutableLaunchMode.Debug);
        var launchConfig = Assert.IsType<ProjectLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(callbackContext));

        Assert.False(launchConfig.DisableLaunchProfile);
        Assert.Equal("http", launchConfig.LaunchProfile);
    }

    [Fact]
    public async Task AddDotnetProject_CustomLaunchToolArgs_ReplaceDotnetRunScaffoldingInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml")
                         .WithLaunchToolArgs(AddCustomLaunchToolArgs, ownedByLaunchConfigurationType: "custom")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal(["tool", "exec", "package", "--yes", "--", "--config", "prod.yaml"], args);
    }

    [Fact]
    public async Task AddDotnetProject_CustomLaunchToolArgs_ReplaceDotnetRunScaffoldingInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml")
                         .WithLaunchToolArgs(AddCustomLaunchToolArgs, ownedByLaunchConfigurationType: "custom")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        Assert.Empty(app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["tool", "exec", "package", "--yes", "--", "--config", "prod.yaml"], args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddDotnetProject_CustomLaunchToolArgs_PreserveLaunchProfileArguments(bool inDebugSession)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDir = Directory.CreateDirectory(Path.Combine(workspace.Path, "MyService"));
        var projectPath = Path.Combine(projectDir.FullName, "MyService.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />");

        var propertiesDir = Directory.CreateDirectory(Path.Combine(projectDir.FullName, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(propertiesDir.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "commandLineArgs": "--profile-arg \"profile value\""
                }
              }
            }
            """);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        if (inDebugSession)
        {
            builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
            builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
            {
                ProtocolsSupported = ["test"],
                SupportedLaunchConfigurations = ["custom"]
            });
        }

        var app = builder.AddDotnetProject("svc", projectPath)
                         .WithArgs("--config", "prod.yaml")
                         .WithLaunchToolArgs(AddCustomLaunchToolArgs, ownedByLaunchConfigurationType: "custom")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal(
            ["tool", "exec", "package", "--yes", "--", "--profile-arg", "profile value", "--config", "prod.yaml"],
            args);
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffolding()
    {
        // When the active IDE advertises support for the "project" launch configuration, the IDE owns the
        // launch (via project_path + launch profile). Emitting `dotnet run …` here would be handed to the IDE
        // as the debugged program's invocation args, so only the user's own args should remain.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["project"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Collection(args,
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg));
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_KeepsDotnetRunArgs_WhenProjectLaunchUnsupported()
    {
        // When the IDE does NOT advertise "project" support, the resource runs as a plain process, so the full
        // `dotnet run --project …` command must be preserved.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["python"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        // run --project <path> [--configuration <cfg>] --no-launch-profile --config prod.yaml
        // (--configuration is only present when the app host assembly declares a build configuration)
        Assert.Equal("run", args[0]);
        Assert.Equal("--project", args[1]);
        Assert.Equal(projectPath, args[2]);
        Assert.Contains("--no-launch-profile", args);
        Assert.Equal("--config", args[^2]);
        Assert.Equal("prod.yaml", args[^1]);
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_KeepsDotnetRunArgs_WhenActiveCustomDebugSupportOffersProcessFallback()
    {
        // SupportsDebugging() consults only the LAST SupportsDebuggingAnnotation. When a caller stacks a
        // custom, non-"project" WithDebugSupport that does NOT rewrite args, ExecutableCreator offers a
        // Process fallback built from Spec.Args. The `dotnet run …` scaffolding must therefore be preserved
        // so that fallback launches the app instead of a bare `dotnet`.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["custom"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        // run --project <path> [--configuration <cfg>] --no-launch-profile --config prod.yaml
        Assert.Equal("run", args[0]);
        Assert.Equal("--project", args[1]);
        Assert.Equal(projectPath, args[2]);
        Assert.Contains("--no-launch-profile", args);
        Assert.Equal("--config", args[^2]);
        Assert.Equal("prod.yaml", args[^1]);
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffolding_WhenActiveCustomDebugSupportOwnsLaunchToolArgs()
    {
        // A stacked custom debug configuration with launch tool arguments owns the tool invocation, so no
        // Process fallback is offered and Spec.Args is composed from that prefix plus the program arguments.
        // The `dotnet run …` scaffolding must be omitted; re-emitting it would duplicate the tool invocation.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["custom"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml")
                         .WithLaunchToolArgs(ctx => ctx.Args.Add("launch-tool-arg"), ownedByLaunchConfigurationType: "custom")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        // Only the custom tool invocation plus the user args remain; no `dotnet run …` scaffolding.
        Assert.Collection(args,
            arg => Assert.Equal("launch-tool-arg", arg),
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg));
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffolding_WhenOwnedLaunchToolArgsAreEmpty()
    {
        // Launch tool argument ownership, rather than the number of values produced, determines who supplies the project
        // launch. A no-op custom tool invocation must still suppress `dotnet run`; DCP consequently cannot offer this
        // IDE-only command line as a Process fallback.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["custom"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml")
                         .WithLaunchToolArgs(static _ => { }, ownedByLaunchConfigurationType: "custom")
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Collection(args,
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg));
    }

    [Theory]
    [InlineData(PersistenceMode.Persistent)]
    [InlineData(PersistenceMode.ParentProcess)]
    [InlineData(PersistenceMode.Resource)]
    public async Task AddDotnetProject_InDebugSession_EffectivePersistentLifetimeKeepsDotnetRunProjectArgs(PersistenceMode persistenceMode)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["project"]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml");

        switch (persistenceMode)
        {
            case PersistenceMode.Persistent:
                app.WithPersistentLifetime();
                break;
            case PersistenceMode.ParentProcess:
                app.WithParentProcessLifetime(Environment.ProcessId);
                break;
            case PersistenceMode.Resource:
                var source = builder.AddContainer("source", "image").WithPersistentLifetime();
                app.WithLifetimeOf(source);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(persistenceMode), persistenceMode, null);
        }

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal("run", args[0]);
        Assert.Equal("--project", args[1]);
        Assert.Equal(projectPath, args[2]);
        Assert.Contains("--no-launch-profile", args);
        Assert.Equal("--config", args[^2]);
        Assert.Equal("prod.yaml", args[^1]);
    }

    [Fact]
    public async Task AddDotnetProject_FileBasedApp_InDebugSession_PersistentLifetimeKeepsDotnetRunFileArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = ["project"]
        });

        var appPath = Path.Combine(builder.AppHostDirectory, "service.cs");
        var app = builder.AddDotnetProject("svc", appPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--flag")
                         .WithPersistentLifetime();

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal("run", args[0]);
        Assert.Equal("--file", args[1]);
        Assert.Equal(appPath, args[2]);
        Assert.Equal("--no-cache", args[3]);
        Assert.Contains("--no-launch-profile", args);
        Assert.Equal("--flag", args[^1]);
    }

    private static void AddCustomLaunchToolArgs(CommandLineArgsCallbackContext context)
    {
        context.Args.Add("tool");
        context.Args.Add("exec");
        context.Args.Add("package");
        context.Args.Add("--yes");
        context.Args.Add("--");
    }

    private static void AssertUnsupportedPublishMessage(
        DistributedApplicationException exception,
        string expectedSubject)
    {
        Assert.StartsWith($"{expectedSubject} Automatic project publishing", exception.Message);
        Assert.All(
            s_unsupportedPublishMessageFragments,
            fragment => Assert.Contains(fragment, exception.Message));
    }

    private static Task ExecutePipelineAsync(DistributedApplication app)
    {
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<DotnetProjectResourceTests>>(),
            CancellationToken.None);

        return pipeline.ExecuteAsync(context);
    }

    private static string CreateProjectDirectory(string workspacePath)
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspacePath, "MyService"));
        CreateFile(projectDirectory.FullName, "MyService.csproj");
        return projectDirectory.FullName;
    }

    private static string CreateFile(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
