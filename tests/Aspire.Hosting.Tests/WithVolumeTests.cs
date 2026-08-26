// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPERSISTENCE001

using Aspire.Hosting.Ats;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithVolumeTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public async Task WithVolumeEnvironmentUsesContainerMountPath(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var container = builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data", env: "DATA_PATH", isReadOnly: true);

        using var app = builder.Build();
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            operation,
            app.Services);

        Assert.Equal("/srv/data", environment["DATA_PATH"]);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("data", mount.Source);
        Assert.Equal("/srv/data", mount.Target);
        Assert.True(mount.IsReadOnly);
    }

    [Fact]
    public async Task WithVolumeEnvironmentUsesWorkloadScopedPathsForProjectAndExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("data", "/srv/data", env: "DATA_PATH");
        var executable = builder.AddExecutable("executable", "test-command", ".")
            .WithVolume("data", "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();
        var expectedProjectPath = VolumeMountPathResolver.GetLocalPath(store, project.Resource, "data");
        var expectedExecutablePath = VolumeMountPathResolver.GetLocalPath(store, executable.Resource, "data");

        Assert.False(Directory.Exists(expectedProjectPath));
        Assert.False(Directory.Exists(expectedExecutablePath));

        var projectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);
        var executableEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            serviceProvider: app.Services);

        Assert.Equal(expectedProjectPath, projectEnvironment["DATA_PATH"]);
        Assert.Equal(expectedExecutablePath, executableEnvironment["DATA_PATH"]);
        Assert.True(Directory.Exists(expectedProjectPath));
        Assert.True(Directory.Exists(expectedExecutablePath));
    }

    [Fact]
    public async Task WithVolumeEnvironmentUsesMountPathForPublishedProjectAndExecutable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("data", "/srv/project", env: "DATA_PATH");
        var executable = builder.AddExecutable("executable", "test-command", ".")
            .WithVolume("data", "/srv/executable", env: "DATA_PATH");

        using var app = builder.Build();
        var projectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            DistributedApplicationOperation.Publish,
            app.Services);
        var executableEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            DistributedApplicationOperation.Publish,
            app.Services);

        Assert.Equal("/srv/project", projectEnvironment["DATA_PATH"]);
        Assert.Equal("/srv/executable", executableEnvironment["DATA_PATH"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void WithVolumeEnvironmentValidatesName(string? env)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var container = builder.AddContainer("container", "image");

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            container.WithVolume("data", "/srv/data", env!));

        Assert.Equal(nameof(env), exception.ParamName);
    }

    [Fact]
    public void WithVolumeEnvironmentRequiresNameForProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null);
        string name = null!;

        var exception = Assert.ThrowsAny<ArgumentException>(() =>
            VolumeResourceBuilderExtensions.WithVolume(project, name, "/srv/data", env: "DATA_PATH"));

        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void ExistingContainerOverloadStillAcceptsPositionalDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var container = builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data", default);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.False(mount.IsReadOnly);
    }

    [Fact]
    public async Task WithVolumeEnvironmentKeepsDistinctFilesystemSafeIdentities()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var executable = builder.AddExecutable("executable", "test-command", ".")
            .WithVolume("Data", "/srv/upper", env: "UPPER_PATH")
            .WithVolume("data", "/srv/lower", env: "LOWER_PATH")
            .WithVolume("../escape", "/srv/escape", env: "ESCAPE_PATH");

        using var app = builder.Build();
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            serviceProvider: app.Services);

        Assert.NotEqual(environment["UPPER_PATH"], environment["LOWER_PATH"]);

        var storePrefix = Path.GetFullPath(app.Services.GetRequiredService<IAspireStore>().BasePath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Assert.All(
            ["UPPER_PATH", "LOWER_PATH", "ESCAPE_PATH"],
            name => Assert.StartsWith(storePrefix, environment[name], comparison));
    }

    [Fact]
    public async Task WithVolumeEnvironmentReusesLocalPathAcrossAppHostRunsAndLifetimes()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var sessionPath = await GetVolumePathAsync(usePersistentLifetime: false);
        var markerPath = Path.Combine(sessionPath, "marker.txt");
        await File.WriteAllTextAsync(markerPath, "persisted");

        var persistentPath = await GetVolumePathAsync(usePersistentLifetime: true);

        Assert.Equal(sessionPath, persistentPath);
        Assert.Equal("persisted", await File.ReadAllTextAsync(Path.Combine(persistentPath, "marker.txt")));

        async Task<string> GetVolumePathAsync(bool usePersistentLifetime)
        {
            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
            builder.Configuration[AspireStore.AspireStorePathKeyName] = workspace.Path;

            var executable = builder.AddExecutable("worker", "test-command", ".")
                .WithVolume("data", "/srv/data", env: "DATA_PATH");
            if (usePersistentLifetime)
            {
                executable.WithPersistentLifetime();
            }
            else
            {
                executable.WithSessionLifetime();
            }

            using var app = builder.Build();
            var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                executable.Resource,
                serviceProvider: app.Services);
            return environment["DATA_PATH"];
        }
    }

    [Fact]
    public void NamedContainerVolumeIdentityAndPersistenceAreIndependentOfContainerLifetime()
    {
        using var sessionBuilder = TestDistributedApplicationBuilder.Create();
        var sessionContainer = sessionBuilder.AddContainer("session", "image")
            .WithSessionLifetime()
            .WithVolume("shared-data", "/srv/data");

        using var persistentBuilder = TestDistributedApplicationBuilder.Create();
        var persistentContainer = persistentBuilder.AddContainer("persistent", "image")
            .WithPersistentLifetime()
            .WithVolume("shared-data", "/srv/data");

        var sessionMount = Assert.Single(sessionContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());
        var persistentMount = Assert.Single(persistentContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("shared-data", sessionMount.Source);
        Assert.Equal(sessionMount.Source, persistentMount.Source);

        var dcpVolume = ContainerVolume.Create("shared-data-resource", sessionMount.Source!);
        Assert.True(dcpVolume.Spec.Persistent);
    }

    [Fact]
    public async Task WithVolumeEnvironmentUsesStorePathForCustomComputeResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var custom = builder.AddResource(new TestComputeResource("custom"))
            .WithVolume("data", "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();
        var expectedPath = VolumeMountPathResolver.GetLocalPath(store, custom.Resource, "data");

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            custom.Resource,
            serviceProvider: app.Services);

        Assert.Equal(expectedPath, environment["DATA_PATH"]);
    }

    [Theory]
    [InlineData("", "data", "DATA_PATH")]
    [InlineData("/data", "", "DATA_PATH")]
    [InlineData("/data", "data", "")]
    public void PolyglotVolumeExportsRejectEmptyArguments(string target, string name, string env)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null);
        var executable = builder.AddExecutable("exe", "cmd", ".");

        Assert.Throws<ArgumentException>(() => CoreExports.WithProjectVolumeForPolyglot(project, target, name, env));
        Assert.Throws<ArgumentException>(() => CoreExports.WithExecutableVolumeForPolyglot(executable, target, name, env));
    }

    [Fact]
    public async Task WithVolumeEnvironmentThrowsWhenComputeEnvironmentCannotMountVolumes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddResource(new MountIncapableComputeEnvironmentResource("env"));
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("data", "/srv/data", env: "DATA_PATH")
            .WithComputeEnvironment(environment);

        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                project.Resource,
                DistributedApplicationOperation.Publish,
                app.Services));

        Assert.Contains("'project'", exception.Message);
        Assert.Contains("volume 'data'", exception.Message);
        Assert.Contains("'DATA_PATH'", exception.Message);
        Assert.Contains("'env'", exception.Message);
    }

    [Fact]
    public async Task WithVolumeEnvironmentResolvesWhenComputeEnvironmentSupportsVolumeMounts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var computeEnvironment = builder.AddResource(new MountCapableComputeEnvironmentResource("env"));
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("data", "/srv/data", env: "DATA_PATH")
            .WithComputeEnvironment(computeEnvironment);

        using var app = builder.Build();
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            DistributedApplicationOperation.Publish,
            app.Services);

        Assert.Equal("/srv/data", environment["DATA_PATH"]);
    }

    [Fact]
    public async Task WithVolumeEnvironmentResolvesWhenComputeEnvironmentIsUnknown()
    {
        // No compute environment is bound, so the target is ambiguous rather than known-unsupported.
        // Publishing must not be blocked on a guess.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("data", "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            DistributedApplicationOperation.Publish,
            app.Services);

        Assert.Equal("/srv/data", environment["DATA_PATH"]);
    }

    private sealed class TestComputeResource(string name) : Resource(name), IComputeResource, IResourceWithEnvironment
    {
    }

    private sealed class MountCapableComputeEnvironmentResource(string name)
        : Resource(name), IComputeEnvironmentResource, IComputeEnvironmentWithVolumeMounts
    {
    }

    private sealed class MountIncapableComputeEnvironmentResource(string name)
        : Resource(name), IComputeEnvironmentResource
    {
    }
}
