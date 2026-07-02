// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectResourceTests
{
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
        using var tempDir = new TestTempDirectory();
        var projectDir = Directory.CreateDirectory(Path.Combine(tempDir.Path, "MyService"));
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
        // detailed "rebuild is required" restart description that ProjectResource gets.
        var resource = new DotnetProjectResource("testapp", AppContext.BaseDirectory);
        resource.AddLifeCycleCommands();

        var restartCommand = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
    }
}
