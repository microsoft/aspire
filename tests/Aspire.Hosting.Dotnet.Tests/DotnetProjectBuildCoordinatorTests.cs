// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001, ASPIREEXTENSION001, ASPIREPIPELINES001

using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectBuildCoordinatorTests(ITestOutputHelper outputHelper)
{
    static DotnetProjectBuildCoordinatorTests()
    {
        EmptyFiles.FileExtensions.AddTextExtension("proj");
    }

    [Fact]
    public async Task MultipleProjectsCreateOneCoordinatedBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");
        var workerPath = Path.Combine(builder.AppHostDirectory, "Worker", "Worker.csproj");

        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        var worker = builder.AddDotnetProjectForPolyglot(
            "worker",
            workerPath,
            new ProjectResourceOptions { ExcludeLaunchProfile = true });

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        using var buildResourceScope = buildResource;
        Assert.Equal([NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)], buildResource.ProjectPaths);
        AssertBuildDependency(api.Resource, buildResource);
        AssertBuildDependency(worker.Resource, buildResource);
        Assert.Equal(
            KnownLaunchConfigurationTypes.ProjectWithExternalBuild,
            Assert.Single(api.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()).LaunchConfigurationType);
        Assert.Equal(
            KnownLaunchConfigurationTypes.ProjectWithExternalBuild,
            Assert.Single(worker.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()).LaunchConfigurationType);
        Assert.Empty(buildResource.Annotations.OfType<ExplicitStartupAnnotation>());
        var hidden = Assert.Single(buildResource.Annotations.OfType<HiddenAnnotation>());
        Assert.Equal(HiddenBehavior.OnCompletion, hidden.Behavior);
        Assert.Equal([0], hidden.SuccessfulExitCodes);
        Assert.Same(
            ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(buildResource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));

        using var app = builder.Build();
        var args = await ArgumentEvaluator.GetArgumentListAsync(buildResource, app.Services);
        var buildProjectPath = Assert.IsType<string>(args[1]);
        Assert.Equal(
            Path.Combine(workspace.Path, ".aspire", "build"),
            buildResource.BuildDirectory);
        Assert.StartsWith(buildResource.BuildDirectory, buildProjectPath, StringComparison.Ordinal);
        Assert.True(File.Exists(buildProjectPath));

        var expected = new List<string> { "build", buildProjectPath };
        AddExpectedConfiguration(builder, expected);
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyProjectDebugSessionDoesNotCreateCoordinatedBuild(bool advertisesProjectCapability)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        if (advertisesProjectCapability)
        {
            builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
            {
                ProtocolsSupported = ["test"],
                SupportedLaunchConfigurations = [KnownLaunchConfigurationTypes.Project]
            });
        }

        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.False(Assert.Single(project.Resource.Annotations.OfType<DotnetProjectMetadata>()).SuppressBuild);
        Assert.Equal(
            KnownLaunchConfigurationTypes.Project,
            Assert.Single(project.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()).LaunchConfigurationType);
        Assert.True(project.Resource.SupportsDebugging(builder.Configuration, out _));
    }

    [Fact]
    public void ExternalBuildDebugSessionCreatesCoordinatedBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = [KnownLaunchConfigurationTypes.ProjectWithExternalBuild]
        });

        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);

        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.True(Assert.Single(project.Resource.Annotations.OfType<DotnetProjectMetadata>()).SuppressBuild);
        Assert.Equal(
            KnownLaunchConfigurationTypes.ProjectWithExternalBuild,
            Assert.Single(project.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()).LaunchConfigurationType);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void DebugSessionWithoutAnObjectCapabilityPayloadUsesLegacyProjectLaunch(string debugSessionInfo)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;

        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            KnownLaunchConfigurationTypes.Project,
            Assert.Single(project.Resource.Annotations.OfType<SupportsDebuggingAnnotation>()).LaunchConfigurationType);
    }

    [Fact]
    public async Task ApplicationServiceProviderDisposesMaterializedBuildResource()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var buildProjectPath = await buildResource.WriteBuildProjectAsync(
            NullLogger.Instance,
            TestContext.Current.CancellationToken);
        var hash = Path.GetFileNameWithoutExtension(buildProjectPath)["projects.".Length..];
        Assert.True(buildResource.IsBuildProjectLeaseActive(hash));

        await app.DisposeAsync();

        Assert.False(buildResource.IsBuildProjectLeaseActive(hash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatedBuildIsIndependentOfWatchMode(bool watchEnabled)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.Configuration["AppHost:Run:WatchEnabled"] = watchEnabled.ToString();

        var project = builder.AddDotnetProject("api", "Api.csproj", options => options.ExcludeLaunchProfile = true);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        AssertBuildDependency(project.Resource, buildResource);
    }

    [Fact]
    public async Task FileOnlyModelDoesNotCreateCoordinatedBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var filePath = Path.Combine(builder.AppHostDirectory, "worker.cs");

        var file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Empty(file.Resource.Annotations.OfType<WaitAnnotation>());

        var args = await ArgumentEvaluator.GetArgumentListAsync(file.Resource);
        var expected = new List<string> { "run", "--file", filePath, "--no-cache" };
        AddExpectedConfiguration(builder, expected);
        expected.Add("--no-launch-profile");
        Assert.Equal(expected, args);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedModelBuildsOnlyProjectsAndGatesFileApp(bool fileFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var projectPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");
        var filePath = Path.Combine(builder.AppHostDirectory, "worker", "worker.cs");

        IResourceBuilder<DotnetProjectResource> project;
        IResourceBuilder<DotnetProjectResource> file;
        if (fileFirst)
        {
            file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);
            project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        }
        else
        {
            project = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
            file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);
        }

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(projectPath)], buildResource.ProjectPaths);
        AssertBuildDependency(project.Resource, buildResource);
        AssertBuildDependency(file.Resource, buildResource);

        var projectArgs = await ArgumentEvaluator.GetArgumentListAsync(project.Resource);
        Assert.Equal("--no-build", projectArgs[3]);

        var fileArgs = await ArgumentEvaluator.GetArgumentListAsync(file.Resource);
        var expectedFileArgs = new List<string> { "run", "--file", filePath, "--no-cache" };
        AddExpectedConfiguration(builder, expectedFileArgs);
        expectedFileArgs.Add("--no-launch-profile");
        Assert.Equal(expectedFileArgs, fileArgs);
    }

    [Fact]
    public void DuplicateProjectPathsProduceOneBuildEntryAndOneWait()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var projectPath = Path.Combine(builder.AppHostDirectory, "Api", "Api.csproj");

        var first = builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        var second = builder.AddDotnetProject("api-copy", projectPath, options => options.ExcludeLaunchProfile = true);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(projectPath)], buildResource.ProjectPaths);
        AssertBuildDependency(first.Resource, buildResource);
        AssertBuildDependency(second.Resource, buildResource);
    }

    [Fact]
    public void PublishModeDoesNotCreateCoordinatedBuildOrSuppressProjectBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddDotnetProject("api", "Api.csproj", options => options.ExcludeLaunchProfile = true);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        var metadata = Assert.Single(project.Resource.Annotations.OfType<DotnetProjectMetadata>());
        Assert.False(metadata.SuppressBuild);
        Assert.Empty(project.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public async Task GeneratedTraversalProjectContainsOnlyUniqueProjectsInModelOrder()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var firstProject = CreateProject(workspace.Path, "First's Project", "First.csproj");
        var secondProject = CreateProject(workspace.Path, "Second", "Second.csproj");
        builder.AddDotnetProject("first", firstProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("second", secondProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("first-copy", firstProject, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        using var buildResourceScope = buildResource;

        var buildProjectPath = await buildResource.WriteBuildProjectAsync(NullLogger.Instance, TestContext.Current.CancellationToken);
        var contents = await File.ReadAllTextAsync(buildProjectPath, TestContext.Current.CancellationToken);
        contents = contents.Replace(
            NormalizeBuildProjectPath(Path.GetRelativePath(buildResource.BuildDirectory, firstProject)).Replace("'", "%27", StringComparison.Ordinal),
            "First%27s Project/First.csproj",
            StringComparison.Ordinal);
        contents = contents.Replace(
            NormalizeBuildProjectPath(Path.GetRelativePath(buildResource.BuildDirectory, secondProject)),
            "Second/Second.csproj",
            StringComparison.Ordinal);

        await Verify(contents, "proj");
    }

    [Fact]
    public async Task GeneratedTraversalProjectUsesAppHostLocalBuildDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(options => options.ProjectDirectory = workspace.Path, outputHelper);
        var projectPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        builder.AddDotnetProject("api", projectPath, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        using var buildResourceScope = buildResource;

        var buildProjectPath = await buildResource.WriteBuildProjectAsync(NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(workspace.Path, ".aspire", "build"), buildResource.BuildDirectory);
        Assert.StartsWith(buildResource.BuildDirectory, buildProjectPath, StringComparison.Ordinal);
        Assert.True(File.Exists(buildProjectPath));
    }

    [Fact]
    public void CaseVariantProjectPathsFollowFilesystemIdentity()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var buildResource = new DotnetProjectBuildResource(
            DotnetProjectBuildCoordinator.BuildResourceName,
            workspace.Path,
            TimeProvider.System);
        var firstPath = CreateProject(workspace.Path, "Service", "App.csproj");
        var caseVariantPath = Path.Combine(workspace.Path, "service", "app.CSPROJ");

        buildResource.AddProject(firstPath);

        if (File.Exists(caseVariantPath))
        {
            buildResource.AddProject(caseVariantPath);
            Assert.Equal([NormalizeProjectPath(firstPath)], buildResource.ProjectPaths);
        }
        else
        {
            var secondPath = CreateProject(workspace.Path, "service", "app.CSPROJ");
            buildResource.AddProject(secondPath);
            Assert.Equal([NormalizeProjectPath(firstPath), NormalizeProjectPath(secondPath)], buildResource.ProjectPaths);
        }
    }

    [Fact]
    public async Task ProjectWithBuildEnvironmentUsesSerializedDirectBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();
        var worker = builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom")
            .WithReference(api);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResources = builder.Resources.OfType<DotnetProjectBuildResource>().ToArray();
        var buildTargets = await Task.WhenAll(buildResources.Select(buildResource =>
            buildResource.GetBuildTargetPathAsync(NullLogger.Instance, TestContext.Current.CancellationToken)));
        Assert.Collection(
            buildResources,
            traversalBuild =>
            {
                Assert.Equal([NormalizeProjectPath(apiPath)], traversalBuild.ProjectPaths);
                Assert.EndsWith(".proj", buildTargets[0], StringComparison.Ordinal);
            },
            directBuild =>
            {
                Assert.Equal([NormalizeProjectPath(workerPath)], directBuild.ProjectPaths);
                Assert.Equal(Path.GetDirectoryName(workerPath), directBuild.WorkingDirectory);
                Assert.Equal(workerPath, buildTargets[1]);
                AssertBuildDependency(directBuild, traversalBuild: buildResources[0]);
            });

        var directBuildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResources[1],
            serviceProvider: app.Services);
        Assert.Collection(
            directBuildEnvironment,
            variable =>
            {
                Assert.Equal("BUILD_FLAVOR", variable.Key);
                Assert.Equal("custom", variable.Value);
            });
        AssertBuildDependency(api.Resource, buildResources[1]);
        AssertBuildDependency(worker.Resource, buildResources[1]);
    }

    [Fact]
    public async Task ProjectsWithServiceDiscoveryReferenceShareTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();
        builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithReference(api);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            [NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)],
            buildResource.ProjectPaths);
    }

    [Fact]
    public async Task ProjectsWithConnectionStringReferenceShareTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var connectionString = builder.AddConnectionString("database");
        builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithReference(connectionString);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            [NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)],
            buildResource.ProjectPaths);
    }

    [Fact]
    public async Task ProjectsWithResourceValuedEnvironmentReferencesShareTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();
        var parameter = builder.AddParameter("setting");
        var connectionString = builder.AddConnectionString("database");
        var externalService = builder.AddExternalService("external", "https://example.com/");
        builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("API_ENDPOINT", api.GetEndpoint("http"))
            .WithEnvironment("SETTING", parameter)
            .WithEnvironment("DATABASE", connectionString)
            .WithEnvironment("EXTERNAL_URL", externalService);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            [NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)],
            buildResource.ProjectPaths);
    }

    [Fact]
    public async Task ProjectWithCustomRuntimeEnvironmentSharesTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment(context => context.EnvironmentVariables["RUNTIME_ONLY"] = "value");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            [NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)],
            buildResource.ProjectPaths);
    }

    [Fact]
    public async Task ProjectWithOrleansReferenceSharesTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var orleans = builder.AddOrleans("orleans")
            .WithDevelopmentClustering();
        builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithReference(orleans);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            [NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)],
            buildResource.ProjectPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedBuildCoexistsWithUserBeforeStartFinalAction(bool registerFinalActionFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var finalActionExecuted = false;

        void RegisterFinalAction() =>
            builder.Pipeline.WithFinalAction(
                WellKnownPipelineSteps.BeforeStart,
                _ =>
                {
                    finalActionExecuted = true;
                    return Task.CompletedTask;
                });

        if (registerFinalActionFirst)
        {
            RegisterFinalAction();
        }

        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true);

        if (!registerFinalActionFirst)
        {
            RegisterFinalAction();
        }

        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(finalActionExecuted);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
    }

    [Fact]
    public async Task BuildEnvironmentAddedByLaterBeforeStartFinalActionFailsBuildEvaluation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true);
        builder.Pipeline.WithFinalAction(
            WellKnownPipelineSteps.BeforeStart,
            _ =>
            {
                project.WithBuildEnvironment("LATE_BUILD_FLAVOR", "custom");
                return Task.CompletedTask;
            });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                buildResource,
                serviceProvider: app.Services));
        Assert.Contains("resource 'worker' changed after the coordinated build plan was materialized", exception.Message);
        Assert.Contains("do not add or remove them after materialization", exception.Message);
    }

    [Fact]
    public async Task BuildEnvironmentAddedAfterInitialBuildFailsRebuildEvaluation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "initial");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        project.WithBuildEnvironment("LATE_BUILD_FLAVOR", "late");

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var rebuildException = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                rebuilder,
                serviceProvider: app.Services));
        var projectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);

        Assert.Contains("resource 'worker' changed after the coordinated build plan was materialized", rebuildException.Message);
        Assert.False(projectEnvironment.ContainsKey("BUILD_FLAVOR"));
        Assert.False(projectEnvironment.ContainsKey("LATE_BUILD_FLAVOR"));
    }

    [Fact]
    public async Task BuildEnvironmentCallbackIsEvaluatedOnceAndExcludedFromRuntimeEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var callbackCount = 0;
        IResource? callbackResource = null;
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment(context =>
            {
                callbackCount++;
                callbackResource = context.Resource;
                context.EnvironmentVariables["BUILD_FLAVOR"] = $"custom-{callbackCount}";
            })
            .WithEnvironment(context => context.EnvironmentVariables["RUNTIME_ONLY"] = "runtime")
            .WithEnvironment("RUNTIME_BUILD_FLAVOR", "runtime-value");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var buildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        var projectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);

        Assert.Equal(1, callbackCount);
        Assert.Same(project.Resource, callbackResource);
        Assert.Equal(["BUILD_FLAVOR"], buildEnvironment.Keys);
        Assert.Equal("custom-1", buildEnvironment["BUILD_FLAVOR"]);
        Assert.False(projectEnvironment.ContainsKey("BUILD_FLAVOR"));
        Assert.Equal("runtime", projectEnvironment["RUNTIME_ONLY"]);
        Assert.Equal("runtime-value", projectEnvironment["RUNTIME_BUILD_FLAVOR"]);
    }

    [Fact]
    public async Task RestartedBuildResourceKeepsTheCoordinatedBuildEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var callbackCount = 0;
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment(context =>
            {
                callbackCount++;
                context.EnvironmentVariables["BUILD_FLAVOR"] = $"custom-{callbackCount}";
            })
            .WithEnvironment(context => context.EnvironmentVariables["RUNTIME_ONLY"] = "runtime");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(buildResource, serviceProvider: app.Services);
        await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(project.Resource, serviceProvider: app.Services);

        // DCP forgets the cached callback results of a resource whenever it restarts. The build environment stays
        // owned by the coordinator, so rebuilding cannot re-run the user callback or change the initial build inputs.
        ForgetCachedEnvironmentResults(project.Resource);
        ForgetCachedEnvironmentResults(buildResource);
        var restartedProjectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);
        var rebuildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);

        Assert.Equal(1, callbackCount);
        Assert.False(restartedProjectEnvironment.ContainsKey("BUILD_FLAVOR"));
        Assert.Equal("runtime", restartedProjectEnvironment["RUNTIME_ONLY"]);
        Assert.Equal(["BUILD_FLAVOR"], rebuildEnvironment.Keys);
        Assert.Equal("custom-1", rebuildEnvironment["BUILD_FLAVOR"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReferenceAndRuntimeEnvironmentOrderingDoesNotAffectTraversalBuild(bool referenceFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var api = builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true)
            .WithHttpEndpoint();
        var worker = builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true);
        if (referenceFirst)
        {
            worker.WithReference(api)
                .WithEnvironment("SETTING", "value");
        }
        else
        {
            worker.WithEnvironment("SETTING", "value")
                .WithReference(api);
        }
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)], buildResource.ProjectPaths);
    }

    [Fact]
    public async Task BuildEnvironmentCallbacksPreserveRemovals()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("TEMPORARY", "temporary")
            .WithBuildEnvironment(context => context.EnvironmentVariables.Remove("TEMPORARY"));
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var buildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);

        Assert.Equal(Array.Empty<string>(), buildEnvironment.Keys.Where(key => key == "TEMPORARY"));
    }

    [Fact]
    public async Task BuildEnvironmentRejectsNonStringValues()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment(context => context.EnvironmentVariables["INVALID"] = 42);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                buildResource,
                serviceProvider: app.Services));
        Assert.Contains("has unsupported value type 'Int32'", exception.Message);
    }

    [Fact]
    public async Task BuildEnvironmentIsRejectedForFileBasedApps()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var appPath = Path.Combine(workspace.Path, "app.cs");
        File.WriteAllText(appPath, "System.Console.WriteLine(\"Hello\");");
        builder.AddDotnetProject("app", appPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom");
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("supported only for project files", exception.Message);
    }

    [Fact]
    public async Task FailedContextSpecificBuildEnvironmentEvaluationIsNotCachedAcrossResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var callbackCount = 0;
        builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment(context =>
            {
                callbackCount++;
                if (callbackCount == 1)
                {
                    throw new InvalidOperationException("Build environment callback failed.");
                }

                context.EnvironmentVariables["BUILD_FLAVOR"] = "custom";
            });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(buildResource, serviceProvider: app.Services));

        // A failed evaluation belongs to the consumer that triggered it; the rebuilder must still be able to obtain
        // the build environment instead of inheriting the faulted result.
        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var rebuildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            rebuilder,
            serviceProvider: app.Services);

        Assert.Equal("Build environment callback failed.", failure.Message);
        Assert.Equal(2, callbackCount);
        Assert.Equal(["BUILD_FLAVOR"], rebuildEnvironment.Keys);
        Assert.Equal("custom", rebuildEnvironment["BUILD_FLAVOR"]);
    }

    [Fact]
    public async Task ContextSpecificBuildEnvironmentIsSharedWithRebuilderAndIdeLaunch()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var callbackCount = 0;
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment(context =>
            {
                callbackCount++;
                context.EnvironmentVariables["BUILD_FLAVOR"] = "build;flavor%";
            })
            .WithEnvironment("BUILD_FLAVOR", "runtime")
            .WithEnvironment(context => context.EnvironmentVariables["RUNTIME_ONLY"] = "runtime");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var runtimeEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            project.Resource,
            ExecutableLaunchMode.Debug,
            new Dictionary<string, string>
            {
                ["BUILD_FLAVOR"] = "runtime",
                ["RUNTIME_ONLY"] = "runtime",
            },
            TestContext.Current.CancellationToken);
        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(
            await project.Resource.CreateLaunchConfigurationAsync(callbackContext));
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var buildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var rebuildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            rebuilder,
            serviceProvider: app.Services);

        Assert.Equal(1, callbackCount);
        Assert.Equal("runtime", runtimeEnvironment["BUILD_FLAVOR"]);
        Assert.Equal("runtime", runtimeEnvironment["RUNTIME_ONLY"]);
        Assert.Equal(["BUILD_FLAVOR"], buildEnvironment.Keys);
        Assert.Equal("build;flavor%", buildEnvironment["BUILD_FLAVOR"]);
        Assert.Equal(["BUILD_FLAVOR"], rebuildEnvironment.Keys);
        Assert.Equal("build;flavor%", rebuildEnvironment["BUILD_FLAVOR"]);
        Assert.NotNull(launchConfiguration.BuildEnvironment);
        Assert.Equal("build;flavor%", launchConfiguration.BuildEnvironment["BUILD_FLAVOR"]);
        Assert.Equal(Path.GetDirectoryName(projectPath), launchConfiguration.BuildWorkingDirectory);
        Assert.Equal(KnownLaunchConfigurationTypes.ProjectWithExternalBuild, launchConfiguration.Type);
    }

    [Fact]
    public async Task LaunchProfileEnvironmentRemainsRuntimeOnlyAndProjectsShareTraversalBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var apiPath = CreateProject(workspace.Path, "Api", "Api.csproj");
        var workerPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        WriteLaunchSettings(apiPath, "api", """
            "PROFILE_VALUE": "api"
            """);
        WriteLaunchSettings(workerPath, "worker", """
            "PROFILE_VALUE": "worker"
            """);

        var api = builder.AddDotnetProject("api", apiPath);
        var worker = builder.AddDotnetProject("worker", workerPath);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var buildEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        var apiEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            api.Resource,
            serviceProvider: app.Services);
        var workerEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            worker.Resource,
            serviceProvider: app.Services);

        Assert.Equal([NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)], buildResource.ProjectPaths);
        Assert.Empty(buildEnvironment);
        Assert.Equal("api", apiEnvironment["PROFILE_VALUE"]);
        Assert.Equal("worker", workerEnvironment["PROFILE_VALUE"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CustomRuntimeWorkingDirectoryDoesNotChangeBuildWorkingDirectory(bool requiresContextSpecificBuild)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Worker", "Worker.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var runtimeWorkingDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "runtime")).FullName;
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithWorkingDirectory(runtimeWorkingDirectory);
        if (requiresContextSpecificBuild)
        {
            project.WithBuildEnvironment("BUILD_FLAVOR", "custom");
        }
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            project.Resource,
            ExecutableLaunchMode.Debug,
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);
        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(
            await project.Resource.CreateLaunchConfigurationAsync(callbackContext));

        Assert.Equal(runtimeWorkingDirectory, project.Resource.WorkingDirectory);
        Assert.Equal(projectDirectory, buildResource.WorkingDirectory);
        Assert.Equal(projectDirectory, launchConfiguration.BuildWorkingDirectory);
    }

    [Fact]
    public async Task CustomRuntimeWorkingDirectoryDoesNotSplitProjectsUnderSharedGlobalJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var sourceRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName;
        File.WriteAllText(Path.Combine(sourceRoot, "global.json"), "{}");
        var apiPath = CreateProject(sourceRoot, "Api", "Api.csproj");
        var workerPath = CreateProject(sourceRoot, "Worker", "Worker.csproj");
        var runtimeWorkingDirectory = Directory.CreateDirectory(Path.Combine(workspace.Path, "runtime")).FullName;
        builder.AddDotnetProject("api", apiPath, options => options.ExcludeLaunchProfile = true);
        var worker = builder.AddDotnetProject("worker", workerPath, options => options.ExcludeLaunchProfile = true)
            .WithWorkingDirectory(runtimeWorkingDirectory);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(apiPath), NormalizeProjectPath(workerPath)], buildResource.ProjectPaths);
        Assert.Equal(sourceRoot, buildResource.WorkingDirectory);
        Assert.Equal(runtimeWorkingDirectory, worker.Resource.WorkingDirectory);
    }

    [Fact]
    public async Task CustomRuntimeWorkingDirectoryDoesNotAdoptForeignGlobalJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(
            Directory.CreateDirectory(Path.Combine(workspace.Path, "source")).FullName,
            "Worker",
            "Worker.csproj");
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var foreignRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "foreign")).FullName;
        File.WriteAllText(Path.Combine(foreignRoot, "global.json"), "{}");
        var runtimeWorkingDirectory = Directory.CreateDirectory(Path.Combine(foreignRoot, "runtime")).FullName;
        var project = builder.AddDotnetProject("worker", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithWorkingDirectory(runtimeWorkingDirectory);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(
            project.Resource,
            ExecutableLaunchMode.Debug,
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);
        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(
            await project.Resource.CreateLaunchConfigurationAsync(callbackContext));

        Assert.Equal(projectDirectory, buildResource.WorkingDirectory);
        Assert.Equal(projectDirectory, launchConfiguration.BuildWorkingDirectory);
        Assert.Equal(runtimeWorkingDirectory, project.Resource.WorkingDirectory);
    }

    [Fact]
    public async Task RemovedProjectIsExcludedFromMaterializedBuildPlan()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var activePath = CreateProject(workspace.Path, "Active", "Active.csproj");
        var removedPath = CreateProject(workspace.Path, "Removed", "Removed.csproj");
        builder.AddDotnetProject("active", activePath, options => options.ExcludeLaunchProfile = true);
        var removed = builder.AddDotnetProject("removed", removedPath, options => options.ExcludeLaunchProfile = true);
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Assert.True(@event.Model.Resources.Remove(removed.Resource));
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(activePath)], buildResource.ProjectPaths);
    }

    [Fact]
    public async Task ReplacedProjectIsExcludedFromMaterializedBuildPlan()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var activePath = CreateProject(workspace.Path, "Active", "Active.csproj");
        var replacedPath = CreateProject(workspace.Path, "Replaced", "Replaced.csproj");
        builder.AddDotnetProject("active", activePath, options => options.ExcludeLaunchProfile = true);
        var replaced = builder.AddDotnetProject("replaced", replacedPath, options => options.ExcludeLaunchProfile = true);
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var index = @event.Model.Resources.IndexOf(replaced.Resource);
            Assert.True(index >= 0);
            @event.Model.Resources[index] = new ExecutableResource(replaced.Resource.Name, "dotnet", workspace.Path);
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(activePath)], buildResource.ProjectPaths);
    }

    [Fact]
    public async Task RemovingEveryProjectRemovesBuildResourceAndFileAppDependency()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Project", "Project.csproj");
        var filePath = Path.Combine(workspace.Path, "worker.cs");
        File.WriteAllText(filePath, "System.Console.WriteLine(\"Hello\");");
        var project = builder.AddDotnetProject("project", projectPath, options => options.ExcludeLaunchProfile = true);
        var file = builder.AddDotnetProject("worker", filePath, options => options.ExcludeLaunchProfile = true);
        var initialBuildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Assert.True(@event.Model.Resources.Remove(project.Resource));
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            Array.Empty<WaitAnnotation>(),
            file.Resource.Annotations
                .OfType<WaitAnnotation>()
                .Where(annotation => ReferenceEquals(annotation.Resource, initialBuildResource))
                .ToArray());
        Assert.Equal(
            Array.Empty<ResourceRelationshipAnnotation>(),
            file.Resource.Annotations
                .OfType<ResourceRelationshipAnnotation>()
                .Where(annotation => ReferenceEquals(annotation.Resource, initialBuildResource))
                .ToArray());
    }

    [Fact]
    public async Task ProjectsWithDifferentGlobalJsonRootsUseSerializedTraversalBuilds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var firstPath = CreateProject(workspace.Path, "First", "First.csproj");
        var secondPath = CreateProject(workspace.Path, "Second", "Second.csproj");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(firstPath)!, "global.json"), """
            {
              "sdk": {
                "version": "1.2.3",
                "rollForward": "disable"
              }
            }
            """);
        var first = builder.AddDotnetProject("first", firstPath, options => options.ExcludeLaunchProfile = true);
        var second = builder.AddDotnetProject("second", secondPath, options => options.ExcludeLaunchProfile = true);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResources = builder.Resources.OfType<DotnetProjectBuildResource>().ToArray();
        var buildTargets = await Task.WhenAll(buildResources.Select(buildResource =>
            buildResource.GetBuildTargetPathAsync(NullLogger.Instance, TestContext.Current.CancellationToken)));
        Assert.Collection(
            buildResources,
            firstBuild =>
            {
                Assert.Equal([NormalizeProjectPath(firstPath)], firstBuild.ProjectPaths);
                Assert.True(File.Exists(Path.Combine(firstBuild.WorkingDirectory, "global.json")));
                Assert.EndsWith(".proj", buildTargets[0], StringComparison.Ordinal);
            },
            secondBuild =>
            {
                Assert.Equal([NormalizeProjectPath(secondPath)], secondBuild.ProjectPaths);
                Assert.Equal(Path.GetDirectoryName(secondPath), secondBuild.WorkingDirectory);
                Assert.EndsWith(".proj", buildTargets[1], StringComparison.Ordinal);
                AssertBuildDependency(secondBuild, traversalBuild: buildResources[0]);
            });
        AssertBuildDependency(first.Resource, buildResources[1]);
        AssertBuildDependency(second.Resource, buildResources[1]);
    }

    [Fact]
    public async Task SymlinkedProjectUsesPhysicalGlobalJsonRootForBuildGrouping()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var physicalRoot = Directory.CreateDirectory(Path.Combine(workspace.Path, "Physical"));
        var linkedProjectPath = CreateProject(physicalRoot.FullName, "Service", "Service.csproj");
        File.WriteAllText(Path.Combine(physicalRoot.FullName, "global.json"), """
            {
              "sdk": {
                "version": "1.2.3",
                "rollForward": "disable"
              }
            }
            """);
        var linkPath = Path.Combine(workspace.Path, "Alias");
        try
        {
            Directory.CreateSymbolicLink(linkPath, physicalRoot.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }

        var aliasProjectPath = Path.Combine(linkPath, "Service", Path.GetFileName(linkedProjectPath));
        var otherProjectPath = CreateProject(workspace.Path, "Other", "Other.csproj");
        builder.AddDotnetProject("linked", aliasProjectPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("other", otherProjectPath, options => options.ExcludeLaunchProfile = true);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResources = builder.Resources.OfType<DotnetProjectBuildResource>().ToArray();
        Assert.Collection(
            buildResources,
            linkedBuild =>
            {
                Assert.Equal([NormalizeProjectPath(aliasProjectPath)], linkedBuild.ProjectPaths);
                Assert.True(File.Exists(Path.Combine(linkedBuild.WorkingDirectory, "global.json")));
            },
            otherBuild =>
            {
                Assert.Equal([NormalizeProjectPath(otherProjectPath)], otherBuild.ProjectPaths);
                Assert.Equal(Path.GetDirectoryName(otherProjectPath), otherBuild.WorkingDirectory);
            });
    }

    [Fact]
    public async Task TraversalBuildPassesCustomAppHostConfigurationDirectlyToProjects()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        var metadata = new DotnetProjectMetadata(projectPath, "DebugLocal");
        var coordinator = DotnetProjectBuildCoordinator.Prepare(builder, metadata);
        var resource = new DotnetProjectResource("service", Path.GetDirectoryName(projectPath)!);
        var resourceBuilder = builder.AddResource(resource).WithAnnotation(metadata);
        DotnetProjectBuildCoordinator.Configure(resourceBuilder, coordinator);
        await using var app = builder.Build();

        await PublishBeforeStartAsync(builder, app);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(buildResource, app.Services);
        Assert.Equal("build", args[0]);
        Assert.EndsWith(".proj", Assert.IsType<string>(args[1]), StringComparison.Ordinal);
        Assert.Equal(["--configuration", "DebugLocal"], args[2..]);
    }

    [Fact]
    public async Task BuildEnvironmentAddedByLaterBeforeStartCallbackUsesDirectBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        var project = builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            project.WithBuildEnvironment("LATE_BUILD_FLAVOR", "custom");
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            projectPath,
            await buildResource.GetBuildTargetPathAsync(
                NullLogger.Instance,
                TestContext.Current.CancellationToken));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        Assert.Equal("custom", environment["LATE_BUILD_FLAVOR"]);
    }

    [Fact]
    public async Task BuildEnvironmentAddedByBeforeStartPipelineStepUsesDirectBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        var project = builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
        builder.Pipeline.AddStep(
            "add-build-environment",
            _ =>
            {
                project.WithBuildEnvironment("PIPELINE_BUILD_FLAVOR", "custom");
                return Task.CompletedTask;
            },
            requiredBy: WellKnownPipelineSteps.BeforeStart);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            projectPath,
            await buildResource.GetBuildTargetPathAsync(
                NullLogger.Instance,
                TestContext.Current.CancellationToken));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        Assert.Equal("custom", environment["PIPELINE_BUILD_FLAVOR"]);
    }

    [Fact]
    public async Task BuildEnvironmentAddedByLifecycleHookUsesDirectBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        var project = builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
#pragma warning disable CS0618 // Lifecycle hooks remain supported and must run before the build-plan pipeline step.
        builder.Services.AddSingleton<IDistributedApplicationLifecycleHook>(
            new CallbackLifecycleHook((_, _) =>
            {
                project.WithBuildEnvironment("LIFECYCLE_BUILD_FLAVOR", "custom");
                return Task.CompletedTask;
            }));
#pragma warning restore CS0618
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(
            projectPath,
            await buildResource.GetBuildTargetPathAsync(
                NullLogger.Instance,
                TestContext.Current.CancellationToken));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource,
            serviceProvider: app.Services);
        Assert.Equal("custom", environment["LIFECYCLE_BUILD_FLAVOR"]);
    }

    [Fact]
    public async Task DuplicateProjectWithProjectSpecificEnvironmentIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("service-copy", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom");
        await using var app = builder.Build();

        var firstException = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartAsync(builder, app));
        var secondException = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartAsync(builder, app));

        Assert.Equal(firstException.Message, secondException.Message);
        Assert.Contains("registered multiple times", firstException.Message, StringComparison.Ordinal);
        Assert.Contains("'service'", firstException.Message, StringComparison.Ordinal);
        Assert.Contains("'service-copy'", firstException.Message, StringComparison.Ordinal);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
    }

    [Fact]
    public async Task BuildPlanAnalysisFailureDoesNotMutateMissingOrInactiveResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "Service.csproj");
        var removedPath = CreateProject(workspace.Path, "Removed", "Removed.csproj");
        var missingPath = Path.Combine(workspace.Path, "Missing", "Missing.csproj");
        builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("service-copy", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom");
        var missing = builder.AddDotnetProject("missing", missingPath, options => options.ExcludeLaunchProfile = true);
        var removed = builder.AddDotnetProject("removed", removedPath, options => options.ExcludeLaunchProfile = true);
        var primaryBuildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.True(builder.Resources.Remove(removed.Resource));
        await using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartAsync(builder, app));

        Assert.True(Assert.Single(missing.Resource.Annotations.OfType<DotnetProjectMetadata>()).SuppressBuild);
        AssertBuildDependency(missing.Resource, primaryBuildResource);
        AssertBuildDependency(removed.Resource, primaryBuildResource);
    }

    [Fact]
    public async Task PartiallyMaterializedBuildPlanIsRolledBackBeforeRetry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var firstPath = CreateProject(workspace.Path, "First", "First.csproj");
        var secondPath = CreateProject(workspace.Path, "Second", "Second.csproj");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(firstPath)!, "global.json"), """
            {
              "sdk": {
                "version": "1.2.3",
                "rollForward": "disable"
              }
            }
            """);
        builder.AddDotnetProject("first", firstPath, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("second", secondPath, options => options.ExcludeLaunchProfile = true);
        var conflictingResource = new ParameterResource(
            $"{DotnetProjectBuildCoordinator.BuildResourceName}-2",
            _ => "conflict");
        conflictingResource.Annotations.Add(NameValidationPolicyAnnotation.None);
        builder.AddResource(conflictingResource);
        var primaryBuildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        var originalProjectPaths = primaryBuildResource.ProjectPaths;
        var originalWorkingDirectory = primaryBuildResource.WorkingDirectory;
        await using var app = builder.Build();

        var firstException = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartAsync(builder, app));
        var secondException = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => PublishBeforeStartAsync(builder, app));

        Assert.Equal(firstException.Message, secondException.Message);
        Assert.Equal(originalProjectPaths, primaryBuildResource.ProjectPaths);
        Assert.Equal(originalWorkingDirectory, primaryBuildResource.WorkingDirectory);
        var executableAnnotation = Assert.Single(primaryBuildResource.Annotations.OfType<ExecutableAnnotation>());
        Assert.Equal("dotnet", executableAnnotation.Command);
        Assert.Equal(originalWorkingDirectory, executableAnnotation.WorkingDirectory);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Null(Assert.Single(
            builder.Resources.OfType<DotnetProjectResource>(),
            resource => resource.Name == "first")
            .Annotations.OfType<DotnetProjectMetadata>().Single().BuildWorkingDirectory);
        Assert.Null(Assert.Single(
            builder.Resources.OfType<DotnetProjectResource>(),
            resource => resource.Name == "second")
            .Annotations.OfType<DotnetProjectMetadata>().Single().BuildWorkingDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymlinkedProjectPathsFollowFilesystemIdentity(bool addAliasFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var projectPath = CreateProject(workspace.Path, "Service", "App.csproj");
        var linkDirectory = Path.Combine(workspace.Path, "ServiceAlias");

        try
        {
            Directory.CreateSymbolicLink(linkDirectory, Path.GetDirectoryName(projectPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }

        var aliasPath = Path.Combine(linkDirectory, Path.GetFileName(projectPath));
        var firstPath = addAliasFirst ? aliasPath : projectPath;
        var secondPath = addAliasFirst ? projectPath : aliasPath;
        var first = builder.AddDotnetProject("first", firstPath, options => options.ExcludeLaunchProfile = true);
        var second = builder.AddDotnetProject("second", secondPath, options => options.ExcludeLaunchProfile = true);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        using var buildResourceScope = buildResource;
        var coordinatedProjectPath = NormalizeProjectPath(firstPath);

        Assert.Equal([coordinatedProjectPath], buildResource.ProjectPaths);
        Assert.Equal(
            coordinatedProjectPath,
            Assert.Single(first.Resource.Annotations.OfType<DotnetProjectMetadata>()).ProjectPath);
        Assert.Equal(
            coordinatedProjectPath,
            Assert.Single(second.Resource.Annotations.OfType<DotnetProjectMetadata>()).ProjectPath);
        Assert.Equal(Path.GetDirectoryName(coordinatedProjectPath), first.Resource.WorkingDirectory);
        Assert.Equal(Path.GetDirectoryName(coordinatedProjectPath), second.Resource.WorkingDirectory);
        Assert.Equal(coordinatedProjectPath, (await ArgumentEvaluator.GetArgumentListAsync(first.Resource))[2]);
        Assert.Equal(coordinatedProjectPath, (await ArgumentEvaluator.GetArgumentListAsync(second.Resource))[2]);
    }

    [Fact]
    public async Task CanceledBuildProjectGenerationCanBeRetried()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var buildResource = new DotnetProjectBuildResource(
            DotnetProjectBuildCoordinator.BuildResourceName,
            workspace.Path,
            TimeProvider.System);
        buildResource.AddProject(CreateProject(workspace.Path, "Api", "Api.csproj"));
        using var canceledCts = new CancellationTokenSource();
        canceledCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => buildResource.WriteBuildProjectAsync(NullLogger.Instance, canceledCts.Token));

        var buildProjectPath = await buildResource.WriteBuildProjectAsync(NullLogger.Instance, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(buildProjectPath));
    }

    [Theory]
    [InlineData(nameof(KnownResourceStates.Finished), null, false)]
    [InlineData(nameof(KnownResourceStates.Finished), -1, true)]
    [InlineData(nameof(KnownResourceStates.Finished), 0, true)]
    [InlineData(nameof(KnownResourceStates.Finished), 1, true)]
    [InlineData(nameof(KnownResourceStates.FailedToStart), null, true)]
    public void BuildCompletionWaitsForSettledExitCode(string state, int? exitCode, bool expected)
    {
        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "Executable",
            State = state,
            ExitCode = exitCode,
            Properties = []
        };

        Assert.Equal(expected, DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(snapshot));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task SharedProjectGraphBuildsOnceBeforeServicesRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sharedProject = CreateSharedProject(workspace.Path);
        var apiProject = CreateConsoleProject(workspace.Path, "Api", sharedProject);
        var workerProject = CreateConsoleProject(workspace.Path, "Worker", sharedProject);
        var apiSentinel = Path.Combine(workspace.Path, "api-ran.txt");
        var workerSentinel = Path.Combine(workspace.Path, "worker-ran.txt");

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("api", apiProject, options => options.ExcludeLaunchProfile = true)
            .WithArgs(apiSentinel);
        builder.AddDotnetProject("worker", workerProject, options => options.ExcludeLaunchProfile = true)
            .WithArgs(workerSentinel);

        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceAsync("api", KnownResourceStates.Finished, completionCts.Token),
                app.ResourceNotifications.WaitForResourceAsync("worker", KnownResourceStates.Finished, completionCts.Token));
        }

        using (var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StopAsync(stopCts.Token);
        }

        Assert.Equal("shared", File.ReadAllText(apiSentinel));
        Assert.Equal("shared", File.ReadAllText(workerSentinel));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(sharedProject)));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(apiProject)));
        Assert.Single(File.ReadAllLines(GetBuildCountPath(workerProject)));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task MultipleBuildGroupsRunSeriallyBeforeServicesStart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstProject = CreateSentinelProject(workspace.Path, "First", """
            <Target Name="RecordFirstBuild" BeforeTargets="CoreCompile">
              <WriteLinesToFile File="$(MSBuildProjectDirectory)/first-built.txt" Lines="built" Overwrite="true" />
            </Target>
            """);
        var secondProject = CreateSentinelProject(workspace.Path, "Second", """
            <Target Name="ValidateBuildOrder" BeforeTargets="CoreCompile">
              <Error Condition="!Exists('$(MSBuildProjectDirectory)/../First/first-built.txt')" Text="The first build group has not completed." />
              <Error Condition="'$(BUILD_FLAVOR)' != 'custom'" Text="The direct build environment was not applied." />
            </Target>
            """);
        var firstSentinel = Path.Combine(workspace.Path, "first-ran.txt");
        var secondSentinel = Path.Combine(workspace.Path, "second-ran.txt");

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("first", firstProject, options => options.ExcludeLaunchProfile = true)
            .WithArgs(firstSentinel);
        builder.AddDotnetProject("second", secondProject, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom")
            .WithArgs(secondSentinel);
        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            var secondBuildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                $"{DotnetProjectBuildCoordinator.BuildResourceName}-2",
                resourceEvent => DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(resourceEvent.Snapshot),
                completionCts.Token);
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceAsync("first", KnownResourceStates.Finished, completionCts.Token),
                app.ResourceNotifications.WaitForResourceAsync("second", KnownResourceStates.Finished, completionCts.Token));

            Assert.Equal(0, secondBuildEvent.Snapshot.ExitCode);
        }

        Assert.Equal(2, builder.Resources.OfType<DotnetProjectBuildResource>().Count());
        Assert.Equal("started", File.ReadAllText(firstSentinel));
        Assert.Equal("started", File.ReadAllText(secondSentinel));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FailedFirstBuildGroupPreventsLaterGroupAndServicesFromStarting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var brokenProject = CreateBrokenProject(workspace.Path, "FIRST_BUILD_FAILED");
        var secondBuildMarker = Path.Combine(workspace.Path, "second-built.txt");
        var secondProject = CreateSentinelProject(workspace.Path, "Second", $$"""
            <Target Name="RecordSecondBuild" BeforeTargets="CoreCompile">
              <WriteLinesToFile File="{{secondBuildMarker}}" Lines="built" Overwrite="true" />
            </Target>
            """);
        var secondSentinel = Path.Combine(workspace.Path, "second-ran.txt");

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("broken", brokenProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("second", secondProject, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom")
            .WithArgs(secondSentinel);
        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var failureCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            var firstBuildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                DotnetProjectBuildCoordinator.BuildResourceName,
                resourceEvent => DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(resourceEvent.Snapshot),
                failureCts.Token);
            var secondBuildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                $"{DotnetProjectBuildCoordinator.BuildResourceName}-2",
                resourceEvent => DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(resourceEvent.Snapshot),
                failureCts.Token);
            await app.ResourceNotifications.WaitForResourceAsync(
                "second",
                KnownResourceStates.FailedToStart,
                failureCts.Token);

            Assert.Equal(KnownResourceStates.Finished, firstBuildEvent.Snapshot.State?.Text);
            Assert.NotEqual(0, Assert.IsType<int>(firstBuildEvent.Snapshot.ExitCode));
            Assert.Equal(KnownResourceStates.FailedToStart, secondBuildEvent.Snapshot.State?.Text);
        }

        Assert.False(File.Exists(secondBuildMarker));
        Assert.False(File.Exists(secondSentinel));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task MissingProjectFailsWithoutBlockingValidSibling()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var validProject = CreateProjectFile(workspace.Path, "Valid", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(validProject)!, "Program.cs"), """
            System.IO.File.WriteAllText(args[0], "started");
            """);
        var sentinelPath = Path.Combine(workspace.Path, "valid-ran.txt");
        var missingProject = Path.Combine(workspace.Path, "Missing", "Missing.csproj");

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("valid", validProject, options => options.ExcludeLaunchProfile = true)
            .WithArgs(sentinelPath);
        var missing = builder.AddDotnetProject("missing", missingProject, options => options.ExcludeLaunchProfile = true);
        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await Task.WhenAll(
                app.ResourceNotifications.WaitForResourceAsync("valid", KnownResourceStates.Finished, completionCts.Token),
                app.ResourceNotifications.WaitForResourceAsync("missing", KnownResourceStates.FailedToStart, completionCts.Token));
        }

        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal([NormalizeProjectPath(validProject)], buildResource.ProjectPaths);
        Assert.False(missing.Resource.Annotations.OfType<DotnetProjectMetadata>().Single().SuppressBuild);
        Assert.DoesNotContain(
            missing.Resource.Annotations.OfType<SupportsDebuggingAnnotation>(),
            annotation => annotation.LaunchConfigurationType == KnownLaunchConfigurationTypes.ProjectWithExternalBuild);
        Assert.Equal("started", File.ReadAllText(sentinelPath));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task MissingProjectInExternalBuildDebugSessionUsesProcessFallback()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new RunSessionInfo
        {
            ProtocolsSupported = ["test"],
            SupportedLaunchConfigurations = [KnownLaunchConfigurationTypes.ProjectWithExternalBuild]
        });
        var missingProject = Path.Combine(workspace.Path, "Missing", "Missing.csproj");
        var missing = builder.AddDotnetProject(
            "missing",
            missingProject,
            options => options.ExcludeLaunchProfile = true);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.False(Assert.Single(missing.Resource.Annotations.OfType<DotnetProjectMetadata>()).SuppressBuild);
        Assert.DoesNotContain(
            missing.Resource.Annotations.OfType<SupportsDebuggingAnnotation>(),
            annotation => annotation.LaunchConfigurationType == KnownLaunchConfigurationTypes.ProjectWithExternalBuild);
        Assert.False(missing.Resource.SupportsDebugging(builder.Configuration, out _));
        var args = await ArgumentEvaluator.GetArgumentListAsync(missing.Resource, app.Services);
        Assert.Equal(["run", "--project", missingProject], args.Take(3));
        Assert.DoesNotContain("--no-build", args);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task BuildEnvironmentIsAppliedToContextSpecificBuild()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProjectFile(workspace.Path, "EnvironmentBuild", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <UseAppHost>false</UseAppHost>
                <BUILD_FLAVOR>project-default</BUILD_FLAVOR>
                <OutputPath Condition="'$(BUILD_FLAVOR)' == 'custom&#x2003;flavor;value%'">bin/custom/</OutputPath>
                <OutputPath Condition="'$(RUNTIME_ONLY)' != ''">bin/wrong/</OutputPath>
              </PropertyGroup>
              <Target Name="ValidateBuildEnvironment" BeforeTargets="CoreCompile">
                <Error Condition="'$(BUILD_FLAVOR)' != 'custom&#x2003;flavor;value%'" Text="BUILD_FLAVOR was not applied to the project build." />
              </Target>
              <Target Name="CustomizeRunCommand" AfterTargets="ComputeRunArguments">
                <PropertyGroup>
                  <RunCommand>&quot;dotnet&quot;</RunCommand>
                  <RunArguments>exec &quot;$(TargetPath)&quot;</RunArguments>
                  <RunWorkingDirectory></RunWorkingDirectory>
                </PropertyGroup>
              </Target>
            </Project>
            """);
        var sentinelPath = Path.Combine(workspace.Path, "environment-build-ran.txt");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "Program.cs"), """
            if (Environment.GetEnvironmentVariable("BUILD_FLAVOR") is not null)
            {
                throw new InvalidOperationException("Build-only environment leaked into the launched process.");
            }

            File.WriteAllText(
                Environment.GetEnvironmentVariable("SENTINEL_PATH")!,
                "started");
            """);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        var project = builder.AddDotnetProject("environment-build", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_FLAVOR", "custom\u2003flavor;value%")
            .WithEnvironment("RUNTIME_ONLY", "runtime")
            .WithEnvironment("SENTINEL_PATH", sentinelPath);
        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.ResourceNotifications.WaitForResourceAsync(
                "environment-build",
                KnownResourceStates.Finished,
                completionCts.Token);
        }

        Assert.Equal("started", File.ReadAllText(sentinelPath));

        var rebuildResult = await app.ResourceCommands.ExecuteCommandAsync(
            project.Resource,
            KnownResourceCommands.RebuildCommand,
            TestContext.Current.CancellationToken);
        Assert.True(rebuildResult.Success);

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task FailedBuildResourceStartDeletesResponseFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = CreateProject(workspace.Path, "EnvironmentBuild", "EnvironmentBuild.csproj");
        var marker = $"FAILED_START_SECRET_{Guid.NewGuid():N}";
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("environment-build", projectPath, options => options.ExcludeLaunchProfile = true)
            .WithBuildEnvironment("BUILD_SECRET", marker);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        buildResource.Annotations.OfType<ExecutableAnnotation>().Single().Command =
            Path.Combine(workspace.Path, "missing-dotnet");
        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.ResourceNotifications.WaitForResourceAsync(
                DotnetProjectBuildCoordinator.BuildResourceName,
                KnownResourceStates.FailedToStart,
                completionCts.Token);
        }

        using var cleanupCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        while (FindResponseFilesContaining(marker).Length > 0)
        {
            await Task.Delay(20, cleanupCts.Token);
        }

        Assert.Empty(FindResponseFilesContaining(marker));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FailedBuildStreamsLogsAndPreventsFileAppFromStarting()
    {
        const string buildLogMarker = "COORDINATED_BUILD_MARKER";

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var brokenProject = CreateBrokenProject(workspace.Path, buildLogMarker);
        var fileApp = Path.Combine(workspace.Path, "worker.cs");
        var sentinel = Path.Combine(workspace.Path, "worker-ran.txt");
        File.WriteAllText(fileApp, """
            #!/usr/bin/env dotnet

            System.IO.File.WriteAllText(
                System.Environment.GetEnvironmentVariable("SENTINEL_PATH")!,
                "started");
            """);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("broken", brokenProject, options => options.ExcludeLaunchProfile = true);
        builder.AddDotnetProject("worker", fileApp, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("SENTINEL_PATH", sentinel);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());

        await using var app = builder.Build();

        using (var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            await app.StartAsync(startCts.Token);
        }

        ResourceEvent buildEvent;
        using (var completionCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            buildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                DotnetProjectBuildCoordinator.BuildResourceName,
                resourceEvent =>
                    KnownResourceStates.TerminalStates.Contains(resourceEvent.Snapshot.State?.Text) &&
                    resourceEvent.Snapshot.ExitCode is not null,
                completionCts.Token);
            var workerState = await app.ResourceNotifications.WaitForResourceAsync(
                "worker",
                [KnownResourceStates.FailedToStart, KnownResourceStates.Exited, KnownResourceStates.Finished],
                completionCts.Token);

            Assert.NotEqual(0, buildEvent.Snapshot.ExitCode);
            Assert.Equal(KnownResourceStates.FailedToStart, workerState);
        }

        using var logsCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var buildLogs = await ReadLogsAsync(
            app.Services.GetRequiredService<ResourceLoggerService>(),
            buildEvent.ResourceId,
            minimumCount: 6,
            logsCts.Token);
        Assert.Contains(buildLogs, line => line.Content.Contains(buildLogMarker, StringComparison.Ordinal));

        Assert.False(File.Exists(sentinel));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task FailedBuildPreventsForceStartedFileAppFromStarting()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var releaseBuild = Path.Combine(workspace.Path, "release-build");
        var brokenProject = CreateGatedBrokenProject(workspace.Path, releaseBuild);
        var fileApp = Path.Combine(workspace.Path, "worker.cs");
        var sentinel = Path.Combine(workspace.Path, "worker-ran.txt");
        File.WriteAllText(fileApp, """
            #!/usr/bin/env dotnet

            System.IO.File.WriteAllText(
                System.Environment.GetEnvironmentVariable("SENTINEL_PATH")!,
                "started");
            """);

        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper).WithResourceCleanUp(true);
        builder.AddDotnetProject("broken", brokenProject, options => options.ExcludeLaunchProfile = true);
        var worker = builder.AddDotnetProject("worker", fileApp, options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("SENTINEL_PATH", sentinel);

        await using var app = builder.Build();

        using var startingCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var workerStartingTask = app.ResourceNotifications.WaitForResourceAsync(
            "worker",
            resourceEvent => resourceEvent.Snapshot.State?.Text == KnownResourceStates.Starting,
            startingCts.Token);

        using var startCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        var startTask = app.StartAsync(startCts.Token);
        var workerStartingEvent = await workerStartingTask;

        // The coordinator callback is intentionally registered in addition to the normal wait edge.
        // Put the instance in the state that the Start command force-releases so this test exercises
        // the callback even when the ordinary dependency wait is bypassed.
        await app.ResourceNotifications.PublishUpdateAsync(
            worker.Resource,
            workerStartingEvent.ResourceId,
            snapshot => snapshot with { State = KnownResourceStates.Waiting });

        using (var forceStartCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        using (var forcedStartingCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            var orchestrator = app.Services.GetRequiredService<ApplicationOrchestratorProxy>();
            var forcedStartingTask = app.ResourceNotifications.WaitForResourceAsync(
                "worker",
                resourceEvent => resourceEvent.Snapshot.State?.Text == KnownResourceStates.Starting,
                forcedStartingCts.Token);
            var forceStartTask = orchestrator.StartResourceAsync(workerStartingEvent.ResourceId, forceStartCts.Token);
            try
            {
                await forcedStartingTask;
            }
            finally
            {
                File.WriteAllText(releaseBuild, string.Empty);
            }

            await forceStartTask;
        }

        await startTask;

        using (var buildCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            var buildEvent = await app.ResourceNotifications.WaitForResourceAsync(
                DotnetProjectBuildCoordinator.BuildResourceName,
                resourceEvent => DotnetProjectBuildCoordinator.IsSettledBuildSnapshot(resourceEvent.Snapshot),
                buildCts.Token);
            Assert.Equal(KnownResourceStates.Finished, buildEvent.Snapshot.State?.Text);
            Assert.NotEqual(0, Assert.IsType<int>(buildEvent.Snapshot.ExitCode));
        }

        using (var failureCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan))
        {
            var workerEvent = await app.ResourceNotifications.WaitForResourceAsync(
                "worker",
                resourceEvent => resourceEvent.Snapshot.State?.Text == KnownResourceStates.FailedToStart,
                failureCts.Token);
            Assert.Equal(KnownResourceStates.FailedToStart, workerEvent.Snapshot.State?.Text);
        }

        Assert.False(File.Exists(sentinel));

        using var stopCts = new CancellationTokenSource(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(stopCts.Token);
    }

    private static string CreateProject(string root, string directoryName, string projectFileName)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, directoryName));
        var path = Path.Combine(directory.FullName, projectFileName);
        File.WriteAllText(path, "<Project />");
        return path;
    }

    private static void WriteLaunchSettings(string projectPath, string profileName, string environmentVariables)
    {
        var propertiesDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties"));
        File.WriteAllText(Path.Combine(propertiesDirectory.FullName, "launchSettings.json"), $$"""
            {
              "profiles": {
                "{{profileName}}": {
                  "commandName": "Project",
                  "environmentVariables": {
                    {{environmentVariables}}
                  }
                }
              }
            }
            """);
    }

    private static string CreateSharedProject(string root)
    {
        var projectPath = CreateProjectFile(root, "Shared", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Target Name="ValidateSolutionIdentity" BeforeTargets="CoreCompile">
                <Error Condition="'$(BuildingSolutionFile)' == 'true'" Text="BuildingSolutionFile must match a direct project build." />
                <Error Condition="'$(CurrentSolutionConfigurationContents)' != ''" Text="CurrentSolutionConfigurationContents must match a direct project build." />
                <Error Condition="'$(SolutionDir)' != '*Undefined*'" Text="SolutionDir must match a direct project build." />
                <Error Condition="'$(SolutionExt)' != '*Undefined*'" Text="SolutionExt must match a direct project build." />
                <Error Condition="'$(SolutionFileName)' != '*Undefined*'" Text="SolutionFileName must match a direct project build." />
                <Error Condition="'$(SolutionName)' != '*Undefined*'" Text="SolutionName must match a direct project build." />
                <Error Condition="'$(SolutionPath)' != '*Undefined*'" Text="SolutionPath must match a direct project build." />
              </Target>
              <Target Name="RecordBuild" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "SharedValue.cs"), """
            namespace Shared;

            public static class SharedValue
            {
                public static string Value => "shared";
            }
            """);
        return projectPath;
    }

    private static string CreateConsoleProject(string root, string name, string sharedProject)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name));
        var relativeSharedProject = Path.GetRelativePath(directory.FullName, sharedProject);
        var projectPath = Path.Combine(directory.FullName, $"{name}.csproj");
        File.WriteAllText(projectPath, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{relativeSharedProject}}" />
              </ItemGroup>
              <Target Name="RecordBuild" BeforeTargets="CoreCompile">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="build" Overwrite="false" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory.FullName, "Program.cs"), """
            using Shared;

            File.WriteAllText(
                args[0],
                SharedValue.Value);
            """);
        return projectPath;
    }

    private static string CreateSentinelProject(string root, string name, string additionalTargets)
    {
        var projectPath = CreateProjectFile(root, name, $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              {{additionalTargets}}
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "Program.cs"), """
            System.IO.File.WriteAllText(args[0], "started");
            """);
        return projectPath;
    }

    private static string CreateGatedBrokenProject(string root, string releaseBuild)
    {
        var projectPath = CreateProjectFile(root, "Broken", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Target Name="WaitThenFailBuild" BeforeTargets="CoreCompile">
                <Exec Command="dotnet run --file &quot;$(MSBuildProjectDirectory)/BuildGate.cs&quot; --no-cache --no-launch-profile" />
              </Target>
            </Project>
            """);
        var releaseBuildBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(releaseBuild));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "BuildGate.cs"), $$"""
            var releaseBuild = System.Text.Encoding.UTF8.GetString(
                System.Convert.FromBase64String("{{releaseBuildBase64}}"));
            while (!System.IO.File.Exists(releaseBuild))
            {
                await System.Threading.Tasks.Task.Delay(10);
            }

            return 1;
            """);
        return projectPath;
    }

    private static string CreateBrokenProject(string root, string buildLogMarker)
    {
        var projectPath = CreateProjectFile(root, "Broken", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <Target Name="EmitBuildMarker" BeforeTargets="CoreCompile">
                <Message Importance="high" Text="{{buildLogMarker}}" />
              </Target>
            </Project>
            """);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, "Program.cs"), "this does not compile");
        return projectPath;
    }

    private static string CreateProjectFile(string root, string name, string contents)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name));
        var projectPath = Path.Combine(directory.FullName, $"{name}.csproj");
        File.WriteAllText(projectPath, contents);
        return projectPath;
    }

    private static string GetBuildCountPath(string projectPath) =>
        Path.Combine(Path.GetDirectoryName(projectPath)!, "build-count.txt");

    private static string NormalizeBuildProjectPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/');

    private static string NormalizeProjectPath(string path) =>
        Path.GetFullPath(path);

    /// <summary>
    /// Emulates what DCP does to a resource when it restarts it.
    /// </summary>
    private static void ForgetCachedEnvironmentResults(IResource resource)
    {
        Assert.True(resource.TryGetEnvironmentVariables(out var environmentCallbacks));
        foreach (var environmentCallback in environmentCallbacks)
        {
            environmentCallback.AsCallbackAnnotation().ForgetCachedResult();
        }
    }

    private static async Task<IReadOnlyList<LogLine>> ReadLogsAsync(
        ResourceLoggerService loggerService,
        string resourceName,
        int minimumCount,
        CancellationToken cancellationToken)
    {
        var logs = new List<LogLine>();
        await foreach (var batch in loggerService.WatchAsync(resourceName).WithCancellation(cancellationToken))
        {
            logs.AddRange(batch);
            if (logs.Count >= minimumCount)
            {
                return logs;
            }
        }

        return logs;
    }

    private static void AssertBuildDependency(
        DotnetProjectResource resource,
        DotnetProjectBuildResource buildResource)
    {
        var wait = Assert.Single(
            resource.Annotations.OfType<WaitAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, buildResource));
        Assert.Equal(WaitType.WaitForCompletion, wait.WaitType);
        Assert.Equal(0, wait.ExitCode);

        Assert.Single(
            resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, buildResource) &&
                          annotation.Type == "WaitFor");
    }

    private static void AssertBuildDependency(
        DotnetProjectBuildResource resource,
        DotnetProjectBuildResource traversalBuild)
    {
        var wait = Assert.Single(
                          resource.Annotations.OfType<WaitAnnotation>(),
                          annotation => ReferenceEquals(annotation.Resource, traversalBuild));
        Assert.Equal(WaitType.WaitForCompletion, wait.WaitType);
    }

    private static async Task PublishBeforeStartAsync(
        IDistributedApplicationBuilder builder,
        DistributedApplication app)
    {
        var coordinator = app.Services.GetRequiredService<DotnetProjectBuildCoordinator.CoordinatorState>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(
                          new BeforeStartEvent(app.Services, model),
                          TestContext.Current.CancellationToken);
        await coordinator.MaterializeBuildPlan(model, app.Services);
    }

    private static string[] FindResponseFilesContaining(string value)
    {
        var matchingFiles = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(Path.GetTempPath(), "aspire-msbuild-*"))
        {
            var responseFilePath = Path.Combine(directory, "build-properties.rsp");
            try
            {
                if (File.Exists(responseFilePath) &&
                    File.ReadAllText(responseFilePath).Contains(value, StringComparison.Ordinal))
                {
                    matchingFiles.Add(responseFilePath);
                }
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                // The owning resource can remove the response file while this test is enumerating it.
            }
        }

        return [.. matchingFiles];
    }

    private static void AddExpectedConfiguration(IDistributedApplicationBuilder builder, List<string> expected)
    {
        if (builder.AppHostAssembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration)
        {
            expected.Add("--configuration");
            expected.Add(configuration);
        }
    }
}
