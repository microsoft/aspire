// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREPERSISTENCE001

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

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
        // detailed "rebuild is required" restart description that ProjectResource gets.
        var resource = new DotnetProjectResource("testapp", AppContext.BaseDirectory);
        resource.AddLifeCycleCommands();

        var restartCommand = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
    }

    [Fact]
    public void AddDotnetProject_DebugAnnotator_ProducesProjectLaunchConfiguration()
    {
        // The "project" SupportsDebuggingAnnotation must produce a ProjectLaunchConfiguration carrying the
        // project path so the IDE (and DCP) can launch/debug it exactly like AddProject.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var projectPath = Path.Combine(builder.AppHostDirectory, "MyService", "MyService.csproj");
        var app = builder.AddDotnetProject("svc", projectPath, o => o.ExcludeLaunchProfile = true);

        Assert.True(app.Resource.TryGetLastAnnotation<SupportsDebuggingAnnotation>(out var supportsDebugging));
        Assert.Equal("project", supportsDebugging.LaunchConfigurationType);

        var exe = Executable.Create("svc", "dotnet");
        supportsDebugging.LaunchConfigurationAnnotator(exe, ExecutableLaunchMode.Debug);

        Assert.True(exe.TryGetProjectLaunchConfiguration(out var launchConfig));
        Assert.Equal("project", launchConfig.Type);
        Assert.Equal(ExecutableLaunchMode.Debug, launchConfig.Mode);
        Assert.Equal(projectPath, launchConfig.ProjectPath);
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
    public async Task AddDotnetProject_InDebugSession_OmitsDotnetRunScaffolding_WhenActiveCustomDebugSupportRewritesArgs()
    {
        // A stacked custom WithDebugSupport with an argsCallback rewrites the arguments for debugging
        // (RewritesArgumentsForDebugging == true), so no Process fallback is offered and that callback owns
        // Spec.Args. The `dotnet run …` scaffolding must be omitted; re-emitting it would pollute the args
        // the custom callback rewrites.
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
                         .WithDebugSupport(_ => new ExecutableLaunchConfiguration("custom"), "custom", ctx => ctx.Args.Add("rewritten-arg"));

        using var application = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource, application.Services);

        // Only the user args plus the custom callback's rewrite remain; no `dotnet run …` scaffolding.
        Assert.Collection(args,
            arg => Assert.Equal("--config", arg),
            arg => Assert.Equal("prod.yaml", arg),
            arg => Assert.Equal("rewritten-arg", arg));
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
}
