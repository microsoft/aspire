// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Kubernetes.Tests;

public class KubernetesPersistentVolumeRunModeTests
{
    [Fact]
    public async Task ProjectAndExecutableUseSharedAspireStoreDirectory()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");
        var executable = builder.AddExecutable("executable", "test-command", ".")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();
        var expectedPath = KubernetesPersistentVolumeLocalStorage.GetPath(store, volume.Resource);

        Assert.False(Directory.Exists(expectedPath));

        var projectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);

        Assert.Equal(expectedPath, projectEnvironment["DATA_PATH"]);
        Assert.True(Directory.Exists(expectedPath));

        var executableEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            serviceProvider: app.Services);

        Assert.Equal(expectedPath, executableEnvironment["DATA_PATH"]);
    }

    [Fact]
    public async Task DifferentVolumesUseDifferentAspireStoreDirectories()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var firstVolume = kubernetes.AddPersistentVolume("first");
        var secondVolume = kubernetes.AddPersistentVolume("second");

        var firstProject = builder.AddProject<Projects.ServiceA>("first-project", launchProfileName: null)
            .WithPersistentVolume(firstVolume, "/srv/data", env: "DATA_PATH");
        var secondProject = builder.AddProject<Projects.ServiceA>("second-project", launchProfileName: null)
            .WithPersistentVolume(secondVolume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var firstEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            firstProject.Resource,
            serviceProvider: app.Services);
        var secondEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            secondProject.Resource,
            serviceProvider: app.Services);

        Assert.NotEqual(firstEnvironment["DATA_PATH"], secondEnvironment["DATA_PATH"]);
    }

    [Fact]
    public async Task SameNamedVolumesInDifferentEnvironmentsUseDifferentAspireStoreDirectories()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var firstEnvironment = builder.AddKubernetesEnvironment("first-env");
        var secondEnvironment = builder.AddKubernetesEnvironment("second-env");
        var firstVolume = firstEnvironment.AddPersistentVolume("data");
        var secondVolume = secondEnvironment.AddPersistentVolume("data");

        var firstProject = builder.AddProject<Projects.ServiceA>("first-project", launchProfileName: null)
            .WithPersistentVolume(firstVolume, "/srv/data", env: "DATA_PATH");
        var secondProject = builder.AddProject<Projects.ServiceA>("second-project", launchProfileName: null)
            .WithPersistentVolume(secondVolume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var firstProjectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            firstProject.Resource,
            serviceProvider: app.Services);
        var secondProjectEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            secondProject.Resource,
            serviceProvider: app.Services);

        Assert.NotEqual(firstProjectEnvironment["DATA_PATH"], secondProjectEnvironment["DATA_PATH"]);
    }

    [Fact]
    public async Task ContainerUsesScopedVolumeAndContainerMountPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var firstContainer = builder.AddContainer("first", "image")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");
        var secondContainer = builder.AddContainer("second", "image")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var expectedVolumeName = VolumeNameGenerator.Generate(volume, "kubernetes-env");
        var firstMount = Assert.Single(firstContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());
        var secondMount = Assert.Single(secondContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());

        Assert.Equal(expectedVolumeName, firstMount.Source);
        Assert.Equal(expectedVolumeName, secondMount.Source);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            firstContainer.Resource,
            serviceProvider: app.Services);

        Assert.Equal("/srv/data", environment["DATA_PATH"]);
    }

    [Fact]
    public async Task NameMatchBindingScopesExistingContainerVolume()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var container = builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data")
            .WithPersistentVolume(volume);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(VolumeNameGenerator.Generate(volume, "kubernetes-env"), mount.Source);
    }

    [Fact]
    public async Task NameMatchBindingIsIndependentOfBuilderOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var container = builder.AddContainer("container", "image")
            .WithPersistentVolume(volume)
            .WithVolume("data", "/srv/data");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(VolumeNameGenerator.Generate(volume, "kubernetes-env"), mount.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameMatchBindingUsesSharedPersistentVolumePathForHostProcesses(bool bindBeforeVolume)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null);

        if (bindBeforeVolume)
        {
            project.WithPersistentVolume(volume)
                .WithVolume("data", "/srv/data", env: "DATA_PATH");
        }
        else
        {
            project.WithVolume("data", "/srv/data", env: "DATA_PATH")
                .WithPersistentVolume(volume);
        }

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            project.Resource,
            serviceProvider: app.Services);

        Assert.Equal(
            KubernetesPersistentVolumeLocalStorage.GetPath(store, volume.Resource),
            environment["DATA_PATH"]);
    }

    [Fact]
    public async Task SameNamedVolumesInDifferentEnvironmentsUseDifferentContainerVolumes()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var firstEnvironment = builder.AddKubernetesEnvironment("first-env");
        var secondEnvironment = builder.AddKubernetesEnvironment("second-env");
        var firstVolume = firstEnvironment.AddPersistentVolume("data");
        var secondVolume = secondEnvironment.AddPersistentVolume("data");

        var firstContainer = builder.AddContainer("first", "image")
            .WithPersistentVolume(firstVolume, "/srv/data");
        var secondContainer = builder.AddContainer("second", "image")
            .WithPersistentVolume(secondVolume, "/srv/data");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var firstMount = Assert.Single(firstContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());
        var secondMount = Assert.Single(secondContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());

        Assert.NotEqual(firstMount.Source, secondMount.Source);
    }

    [Fact]
    public void ExistingPersistentVolumeOverloadStillAcceptsPositionalDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var container = builder.AddContainer("container", "image")
            .WithPersistentVolume(volume, "/srv/data", default);

        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.False(mount.IsReadOnly);
    }

    [Fact]
    public async Task MixedContainerAndExecutableConsumersAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        builder.AddExecutable("executable", "test-command", ".")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");
        builder.AddContainer("container", "image")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("both local container and host-process resources", exception.Message);
        Assert.Contains("data", exception.Message);
        Assert.Contains("executable", exception.Message);
        Assert.Contains("container", exception.Message);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(
        DistributedApplication app,
        CancellationToken cancellationToken);
}
