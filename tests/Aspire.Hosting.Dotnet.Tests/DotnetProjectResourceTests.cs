// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPERSISTENCE001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREPROJECTS001

using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectResourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task AddDotnetProject_ProjectFile_ProducesDotnetRunProjectArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var expected = new List<string> { "run", "--project", projectPath, "--no-build" };
        AddExpectedConfiguration(builder, expected);
        expected.Add("--no-launch-profile");
        Assert.Equal(expected, args);
    }

    [Fact]
    public async Task AddDotnetProject_FileBasedApp_ProducesDotnetRunFileArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var appPath = Path.Combine(builder.AppHostDirectory, "service.cs");
        var app = builder.AddDotnetProject("svc", appPath, o => o.ExcludeLaunchProfile = true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var expected = new List<string> { "run", "--file", appPath, "--no-build" };
        AddExpectedConfiguration(builder, expected);
        expected.Add("--no-launch-profile");
        Assert.Equal(expected, args);
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
    public void AddDotnetProject_ResourceSupportsServiceDiscoveryAndIsComputeResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddDotnetProject("svc", "MyService.csproj", o => o.ExcludeLaunchProfile = true);

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
        Assert.IsAssignableFrom<ExecutableResource>(app.Resource);
        Assert.IsAssignableFrom<IComputeResource>(app.Resource);
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
    public async Task AddDotnetProject_InPublishMode_ProducesProjectManifest(string appKind)
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

        var manifest = await ManifestUtils.GetManifest(app.Resource, workspace.Path);
        var metadata = app.Resource.GetProjectMetadata();
        var expectedPath = Path.GetRelativePath(workspace.Path, metadata.ProjectPath).Replace('\\', '/');

        Assert.Equal("project.v0", manifest["type"]?.GetValue<string>());
        Assert.Equal(expectedPath, manifest["path"]?.GetValue<string>());
        Assert.Null(manifest["args"]);
        Assert.True(app.Resource.SupportsDotnetProgramPublishing());
        Assert.IsAssignableFrom<IDotnetProgramResource>(app.Resource);
        Assert.IsAssignableFrom<IContainerFilesDestinationResource>(app.Resource);
    }

    [Fact]
    public void AddDotnetProject_InPublishMode_ConfiguresSdkPublishingWithoutCoordinatedBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateFile(workspace.Path, "MyService.csproj");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);
        var metadata = resource.Resource.GetProjectMetadata();

        Assert.True(resource.Resource.SupportsDotnetProgramPublishing());
        Assert.False(metadata.SuppressBuild);
        Assert.DoesNotContain(builder.Resources, candidate => candidate is DotnetProjectBuildResource);
        Assert.Single(resource.Resource.Annotations.OfType<PipelineStepAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<PipelineConfigurationAnnotation>());
        Assert.Single(resource.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
    }

    [Fact]
    public async Task DirectlyConstructedDotnetProjectResource_ManifestRequiresPublishingConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var resource = new DotnetProjectResource("svc", workspace.Path);
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ManifestUtils.GetManifest(resource, workspace.Path));

        Assert.Equal(
            "The .NET program resource 'svc' is not configured for publishing. " +
            "Create it with a supported builder API or call WithDotnetProgramPublishing() after attaching project metadata.",
            exception.Message);
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
    [RequiresFeature(TestFeature.ContainerImageBuild)]
    public async Task AddDotnetProject_FileBasedAppWithContainerFilesBuildsContainerArchive()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = Path.Combine(workspace.Path, "app.cs");
        await File.WriteAllTextAsync(appPath, """
            #:property PublishAot=false

            Console.WriteLine("file app");
            """);
        var archivePath = Path.Combine(workspace.Path, "app.tar.gz");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var assets = builder.AddContainer("assets", "mcr.microsoft.com/dotnet/runtime", "10.0")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/etc/os-release" });
        var resource = builder.AddDotnetProject("file-app", appPath, options => options.ExcludeLaunchProfile = true)
            .WithAnnotation(new ContainerFilesDestinationAnnotation
            {
                Source = assets.Resource,
                DestinationPath = "/app/assets"
            })
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(archivePath));
        Assert.True(new FileInfo(archivePath).Length > 0);
    }

    [Fact]
    [RequiresFeature(TestFeature.ContainerImageBuild)]
    public async Task AddDotnetProject_ProjectBuildEnvironmentFlowsToSdkPublish()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "project"));
        var projectPath = Path.Combine(projectDirectory.FullName, "Project.csproj");
        await File.WriteAllTextAsync(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="ValidateBuildFlavor" BeforeTargets="Publish">
                <Error Condition="'$(BUILD_FLAVOR)' != 'custom'" Text="BUILD_FLAVOR was not provided." />
              </Target>
            </Project>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory.FullName, "Program.cs"),
            """System.Console.WriteLine("project");""");
        var archivePath = Path.Combine(workspace.Path, "project.tar.gz");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddDotnetProject("project", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom")
            .WithContainerBuildOptions(context =>
            {
                context.Destination = ContainerImageDestination.Archive;
                context.ImageFormat = ContainerImageFormat.Docker;
                context.OutputPath = archivePath;
                context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64;
            });
        using var app = builder.Build();
        var imageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>();

        await imageBuilder.BuildImageAsync(resource.Resource, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(archivePath));
        Assert.Empty(resource.Resource.GetProjectMetadata().BuildEnvironment);
        Assert.Null(resource.Resource.GetProjectMetadata().BuildWorkingDirectory);
    }

    [Fact]
    public void AddDotnetProject_PublishAsDockerFile_UsesProjectContextAndProjectPortDefaults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "project"));
        var projectPath = CreateFile(projectDirectory.FullName, "MyService.csproj");
        var runtimeDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "runtime"));
        File.WriteAllText(Path.Combine(projectDirectory.FullName, "Dockerfile"), "FROM scratch");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
            .WithWorkingDirectory(runtimeDirectory.FullName)
            .WithHttpEndpoint()
            .PublishAsDockerFile();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var dockerfile = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());
        var endpoint = Assert.Single(container.Annotations.OfType<EndpointAnnotation>());

        Assert.Equal(projectDirectory.FullName, dockerfile.ContextPath);
        Assert.Equal(8080, endpoint.TargetPort);
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

    [Theory]
    [InlineData("MyService.csproj")]
    [InlineData("service.cs")]
    public async Task AddDotnetProject_RebuilderUsesConfiguredBuildConfiguration(string projectFileName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", projectFileName);
        var project = builder.AddDotnetProject("svc", projectPath, options => options.ExcludeLaunchProfile = true);
        var launchDefaults = Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        launchDefaults.BuildConfiguration = "Release";

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder);

        Assert.Equal(
            [
                "build",
                projectPath,
                "--configuration",
                "Release"
            ],
            args);
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
    public void AddLifeCycleCommands_FileBasedApp_AddsRebuildCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var appPath = Path.Combine(builder.AppHostDirectory, "service.cs");
        var resource = builder.AddDotnetProject("testapp", appPath, o => o.ExcludeLaunchProfile = true).Resource;
        resource.AddLifeCycleCommands();

        Assert.Contains(
            resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == KnownResourceCommands.RebuildCommand);
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
        // The versioned external-build capability prevents an older IDE from accepting the resource while ignoring
        // coordinated-build metadata. The producer still resolves the full project launch configuration.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));
        Assert.Equal(KnownLaunchConfigurationTypes.ProjectWithExternalBuild, supportsDebugging.LaunchConfigurationType);

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            app.Resource,
            ExecutableLaunchMode.Debug);
        var launchConfig = Assert.IsType<ProjectLaunchConfiguration>(
            await app.Resource.CreateLaunchConfigurationAsync(callbackContext));
        Assert.Equal(KnownLaunchConfigurationTypes.ProjectWithExternalBuild, launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
        Assert.Equal(projectPath, launchConfig.ProjectPath);
        Assert.True(launchConfig.DisableLaunchProfile);
        Assert.Equal(string.Empty, launchConfig.LaunchProfile);
        Assert.Equal(
            builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            launchConfig.BuildConfiguration);
        Assert.True(launchConfig.SuppressBuild);
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
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffoldingWhenExternalBuildIsSupported()
    {
        // The versioned capability guarantees that the IDE honors coordinated-build values and suppression.
        // Emitting `dotnet run …` as well would hand those tool arguments to the debugged program.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = [KnownLaunchConfigurationTypes.ProjectWithExternalBuild]
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
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffoldingWhenLegacyProjectLaunchIsSupported()
    {
        // An IDE that supports only ordinary project launch owns both building and launching the resource. The
        // coordinated-build path is disabled for this session, so only the user's program arguments are emitted.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = [KnownLaunchConfigurationTypes.Project]
        });

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true)
                         .WithArgs("--config", "prod.yaml");

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        Assert.Equal(["--config", "prod.yaml"], args);
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_KeepsDotnetRunArgs_WhenProjectLaunchUnsupported()
    {
        // When the IDE does not advertise project support, the resource runs as a plain process, so the full
        // `dotnet run --project ...` command must be preserved.
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
        Assert.Equal("run", args[0]);
        Assert.Equal("--project", args[1]);
        Assert.Equal(projectPath, args[2]);
        Assert.Contains("--no-launch-profile", args);
        Assert.Equal("--config", args[^2]);
        Assert.Equal("prod.yaml", args[^1]);
    }

    [Fact]
    public async Task AddDotnetProject_InDebugSession_KeepsDotnetRunArgs_WhenActiveCustomDebugSupportDoesNotOwnInvocation()
    {
        // SupportsDebugging() consults only the LAST SupportsDebuggingAnnotation. When a caller stacks a
        // custom, non-"project" WithDebugSupport that does not own the .NET SDK invocation, the selected launch
        // still needs the complete `dotnet run …` scaffolding.
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
        // A stacked custom debug configuration with launch tool arguments owns the tool invocation.
        // The `dotnet run …` scaffolding must be omitted; re-emitting it would duplicate that invocation.
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
        // Launch tool argument ownership, rather than the number of values produced, determines who supplies the
        // project launch. A no-op custom tool invocation must still suppress `dotnet run`.
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

    private static void AddExpectedConfiguration(IDistributedApplicationBuilder builder, List<string> expected)
    {
        if (builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration)
        {
            expected.Add("--configuration");
            expected.Add(configuration);
        }
    }

    private static async Task ExecutePipelineAsync(DistributedApplication app)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TestConstants.LongTimeoutTimeSpan);
        var pipeline = app.Services.GetRequiredService<IDistributedApplicationPipeline>();
        var context = new PipelineContext(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
            app.Services,
            app.Services.GetRequiredService<ILogger<DotnetProjectResourceTests>>(),
            cts.Token);

        await pipeline.ExecuteAsync(context).WaitAsync(cts.Token);
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
