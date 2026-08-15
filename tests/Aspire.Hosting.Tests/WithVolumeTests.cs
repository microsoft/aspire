// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class WithVolumeTests
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
}
