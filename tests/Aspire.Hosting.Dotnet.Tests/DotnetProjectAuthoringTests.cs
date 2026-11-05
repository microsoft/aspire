// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001
#pragma warning disable ASPIREEXTENSION001

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectAuthoringTests(ITestOutputHelper output)
{
    private const string ExternalBuildCapability = "test-framework-with-external-build.v1";

    [Fact]
    public void FactoryPreservesResourceAndManagedMetadataBeforeConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var path = DotnetProjectTestHelpers.CreateProject(workspace.Path, "Worker");
        TestDotnetProjectResource? created = null;
        IProjectMetadata? configuredMetadata = null;

        var resource = builder.AddDotnetProject(
            "worker", path,
            (name, directory) => created = new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            configured =>
            {
                Assert.Same(created, configured.Resource);
                configuredMetadata = configured.Resource.GetProjectMetadata();
                Assert.Equal(path, configuredMetadata.ProjectPath);
                configured.WithDotnetBaseDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true });
                configured.WithIconName("Flash");
            },
            PreserveRunArguments);

        Assert.Same(created, resource.Resource);
        Assert.Same(configuredMetadata, resource.Resource.GetProjectMetadata());
        Assert.Same(created, Assert.Single(builder.Resources, candidate => candidate.Name == "worker"));
        Assert.True(resource.Resource.SupportsDotnetProgramPublishing());
        Assert.Single(resource.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        Assert.Empty(resource.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
    }

    [Fact]
    public void BaseDefaultsDoNotDelegateToAnOrdinaryProjectDebugger()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new
        {
            protocols_supported = new[] { "test" },
            supported_launch_configurations = new[] { "project", "project-with-external-build.v1" }
        });
        var path = DotnetProjectTestHelpers.CreateProject(workspace.Path, "Worker");
        var resource = builder.AddDotnetProject(
            "worker", path,
            static (name, directory) => new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            resource => resource.WithDotnetBaseDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true }),
            PreserveRunArguments);

        Assert.Empty(resource.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.False(resource.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.True(resource.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("project-with-external-build.v1", false)]
    [InlineData(ExternalBuildCapability, true)]
    [InlineData("azure-functions", false)]
    public void CoordinationRequiresTheSpecializedCapability(string? advertisedCapability, bool expectedCoordination)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        if (advertisedCapability is not null)
        {
            builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
            builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new
            {
                protocols_supported = new[] { "test" },
                supported_launch_configurations = new[] { advertisedCapability, "test-framework" }
            });
        }

        var path = DotnetProjectTestHelpers.CreateProject(workspace.Path, "Worker");
        var resource = AddSpecialized(builder, path);

        Assert.Equal(expectedCoordination, resource.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Equal(expectedCoordination ? 1 : 0, builder.Resources.OfType<DotnetProjectBuildResource>().Count());
    }

    [Fact]
    public void ProcessFallbackBuildsWhenIdeCannotLaunchSpecializedResource()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        builder.Configuration["DEBUG_SESSION_PORT"] = "localhost:12345";
        builder.Configuration[KnownConfigNames.DebugSessionInfo] = JsonSerializer.Serialize(new
        {
            protocols_supported = new[] { "test" },
            supported_launch_configurations = new[] { "unrelated" }
        });
        var path = DotnetProjectTestHelpers.CreateProject(workspace.Path, "Worker");
        var resource = AddSpecialized(builder, path);

        Assert.False(resource.Resource.SupportsDebugging(builder.Configuration, out _));
        Assert.True(resource.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Single(resource.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
    }

    [Fact]
    public async Task FactoryTransformsSdkRunArgumentsAfterCoordinatedBuild()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            output);
        var path = DotnetProjectTestHelpers.CreateProject(workspace.Path, "Worker");
        string? selectedCommand = null;
        IReadOnlyList<string>? selectedArguments = null;
        var resource = builder.AddDotnetProject(
            "worker", path,
            static (name, directory) => new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            configured => configured.WithDotnetBaseDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true }),
            (project, command, arguments) =>
            {
                selectedCommand = command;
                selectedArguments = arguments;
                return arguments.Where(argument => argument != "--integration-owned").ToArray();
            });
        var metadata = Assert.Single(resource.Resource.Annotations.OfType<DotnetProjectMetadata>());
        metadata.RunPropertiesResolver = (_, _, _, _, _, _) =>
            Task.FromResult(new DotnetProjectRunProperties(
                "selected-tool",
                "--integration-owned --sdk-value",
                Path.GetDirectoryName(path),
                Path.Combine(Path.GetDirectoryName(path)!, "bin", "Worker.dll"),
                UseAppHost: false));
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var buildResource = Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        await app.ResourceNotifications.PublishUpdateAsync(
            buildResource,
            snapshot => snapshot with
            {
                State = KnownResourceStates.Finished,
                ExitCode = 0,
            });

        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services);

        Assert.Equal("selected-tool", selectedCommand);
        Assert.Equal(["--integration-owned", "--sdk-value"], selectedArguments);
        Assert.Equal(["--sdk-value"], arguments);
        Assert.Equal("selected-tool", resource.Resource.Command);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("wrong-name")]
    [InlineData("registered")]
    public void InvalidFactoryResultIsRejected(string kind)
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var exception = Assert.Throws<InvalidOperationException>(() => builder.AddDotnetProject<TestDotnetProjectResource>(
            "worker", "Worker.csproj",
            (name, directory) =>
            {
                if (kind == "null")
                {
                    return null!;
                }

                var resource = new TestDotnetProjectResource(kind == "wrong-name" ? "other" : name, directory);
                if (kind == "registered")
                {
                    builder.AddResource(resource);
                }

                return resource;
            },
            ExternalBuildCapability,
            _ => { },
            PreserveRunArguments));

        Assert.Contains("unregistered resource with the requested name", exception.Message);
    }

    [Fact]
    public void CallbackCannotRemoveManagedMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);
        Assert.Throws<InvalidOperationException>(() => builder.AddDotnetProject(
            "worker", "Worker.csproj",
            static (name, directory) => new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            resource => resource.Resource.Annotations.Remove(resource.Resource.GetProjectMetadata()),
            PreserveRunArguments));
    }

    [Fact]
    public void FactoryValidatesRunArgumentTransform()
    {
        using var builder = TestDistributedApplicationBuilder.Create(output);

        var exception = Assert.Throws<ArgumentNullException>(() => builder.AddDotnetProject(
            "worker", "Worker.csproj",
            static (name, directory) => new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            _ => { },
            transformRunArguments: null!));

        Assert.Equal("transformRunArguments", exception.ParamName);
    }

    [Fact]
    public void AuthoringFactoryIsExperimentalAndNotExported()
    {
        var method = Assert.Single(typeof(DotnetProjectHostingExtensions).GetMethods(),
            candidate => candidate.Name == nameof(DotnetProjectHostingExtensions.AddDotnetProject) &&
                candidate.IsGenericMethodDefinition);
        var attribute = Assert.IsType<ExperimentalAttribute>(method.GetCustomAttribute<ExperimentalAttribute>());

        Assert.Equal("ASPIREPROJECTS001", attribute.DiagnosticId);
        Assert.NotNull(method.GetCustomAttribute<AspireExportIgnoreAttribute>());
    }

    private static IResourceBuilder<TestDotnetProjectResource> AddSpecialized(IDistributedApplicationBuilder builder, string path)
    {
        return builder.AddDotnetProject(
            "worker", path,
            static (name, directory) => new TestDotnetProjectResource(name, directory),
            ExternalBuildCapability,
            resource =>
            {
                resource.WithDotnetBaseDefaults(new ProjectResourceOptions { ExcludeLaunchProfile = true });
                var launchType = resource.Resource.GetProjectMetadata().SuppressBuild ? ExternalBuildCapability : "test-framework";
                resource.WithDebugSupport(mode => new { type = launchType, mode }, launchType);
            },
            PreserveRunArguments);
    }

    private static IReadOnlyList<string> PreserveRunArguments(
        TestDotnetProjectResource resource,
        string command,
        IReadOnlyList<string> arguments) => arguments;
}
