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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneResourceBoundToSameNamedVolumesInDifferentEnvironmentsIsRejected(bool nameMatchSpelling)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var firstEnvironment = builder.AddKubernetesEnvironment("first-env");
        var secondEnvironment = builder.AddKubernetesEnvironment("second-env");
        var firstVolume = firstEnvironment.AddPersistentVolume("data");
        var secondVolume = secondEnvironment.AddPersistentVolume("data");

        // Both volumes are named "data", so neither the local path lookup nor the container mount
        // rewrite can tell the two bindings apart. Only run mode can express this: AddPersistentVolume
        // registers the volume in publish mode and the duplicate name is rejected while the model is
        // built, while run mode hands back a CreateResourceBuilder that never registers it.
        var project = builder.AddProject<Projects.ServiceA>("project", launchProfileName: null);

        if (nameMatchSpelling)
        {
            project.WithVolume("data", "/srv/first", env: "FIRST_PATH")
                .WithPersistentVolume(firstVolume)
                .WithVolume("data", "/srv/second", env: "SECOND_PATH")
                .WithPersistentVolume(secondVolume);
        }
        else
        {
            project.WithPersistentVolume(firstVolume, "/srv/first", env: "FIRST_PATH")
                .WithPersistentVolume(secondVolume, "/srv/second", env: "SECOND_PATH");
        }

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("binds 2 different Kubernetes persistent volumes named 'data'", exception.Message);
        Assert.Contains("'first-env'", exception.Message);
        Assert.Contains("'second-env'", exception.Message);
        Assert.Contains("project", exception.Message);
    }

    [Fact]
    public async Task OneContainerBoundToSameNamedVolumesInDifferentEnvironmentsIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var firstEnvironment = builder.AddKubernetesEnvironment("first-env");
        var secondEnvironment = builder.AddKubernetesEnvironment("second-env");
        var firstVolume = firstEnvironment.AddPersistentVolume("data");
        var secondVolume = secondEnvironment.AddPersistentVolume("data");

        builder.AddContainer("container", "image")
            .WithPersistentVolume(firstVolume, "/srv/first", env: "FIRST_PATH")
            .WithPersistentVolume(secondVolume, "/srv/second", env: "SECOND_PATH");

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("binds 2 different Kubernetes persistent volumes named 'data'", exception.Message);
        Assert.Contains("container", exception.Message);
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
    public async Task NameMatchBindingPreservesExistingContainerVolume()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var container = builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data")
            .WithPersistentVolume(volume);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        // The name-match overload predates the env convention, so it must keep mounting the volume
        // the container already declared. Scoping it would strand data written by earlier runs.
        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("data", mount.Source);
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
        Assert.Equal("data", mount.Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameMatchBindingWithEnvScopesContainerVolume(bool bindBeforeVolume)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");
        var container = builder.AddContainer("container", "image");

        if (bindBeforeVolume)
        {
            container.WithPersistentVolume(volume)
                .WithVolume("data", "/srv/data", env: "DATA_PATH");
        }
        else
        {
            container.WithVolume("data", "/srv/data", env: "DATA_PATH")
                .WithPersistentVolume(volume);
        }

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        // Spelling env on the mount is the same opt-in as spelling it on the binding, so it has to
        // produce the same worktree-scoped volume. The scoping decision runs at finalization, which
        // is what keeps this independent of the order the two calls were made in.
        var expectedVolumeName = VolumeNameGenerator.Generate(volume, "kubernetes-env");
        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal(expectedVolumeName, mount.Source);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container.Resource,
            serviceProvider: app.Services);

        Assert.Equal("/srv/data", environment["DATA_PATH"]);
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
            .WithPersistentVolume(firstVolume, "/srv/data", env: "DATA_PATH");
        var secondContainer = builder.AddContainer("second", "image")
            .WithPersistentVolume(secondVolume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var firstMount = Assert.Single(firstContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());
        var secondMount = Assert.Single(secondContainer.Resource.Annotations.OfType<ContainerMountAnnotation>());

        Assert.NotEqual(firstMount.Source, secondMount.Source);
    }

    [Fact]
    public async Task MountPathBindingWithoutEnvPreservesPersistentVolumeName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        var container = builder.AddContainer("container", "image")
            .WithPersistentVolume(volume, "/srv/data");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        // Scoping is opt-in via env. Without it this overload has to mount the same local volume it
        // mounted in 13.5.0, otherwise upgrading silently repoints the container at empty storage.
        var mount = Assert.Single(container.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("data", mount.Source);
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

    [Fact]
    public async Task PublishOnlyBindingsAllowMixedContainerAndProjectConsumers()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        // Neither binding asks for the run-mode environment path, so the project materializes no
        // local backing store and cannot conflict with the container's named volume. This shape
        // predates the env overload and must keep working.
        builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithPersistentVolume(volume, "/srv/data");
        builder.AddContainer("container", "image")
            .WithPersistentVolume(volume, "/srv/data");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NameMatchBindingWithEnvVolumeConflictsWithContainerConsumer(bool bindBeforeVolume)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        // The env lives on the mount rather than the binding here, so the conflict is only visible by
        // inspecting the resource. Both orders must detect it.
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

        builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data")
            .WithPersistentVolume(volume);

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("both local container and host-process resources", exception.Message);
        Assert.Contains("project", exception.Message);
        Assert.Contains("container", exception.Message);
    }

    [Fact]
    public async Task EnvVolumeForDifferentNameDoesNotConflictWithContainerConsumer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        // The env mount names a different volume, so it resolves an unrelated local directory and
        // never touches this persistent volume's store path.
        builder.AddProject<Projects.ServiceA>("project", launchProfileName: null)
            .WithVolume("scratch", "/srv/scratch", env: "SCRATCH_PATH")
            .WithPersistentVolume(volume);
        builder.AddContainer("container", "image")
            .WithVolume("data", "/srv/data")
            .WithPersistentVolume(volume);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NameMatchBindingRejectsUnsupportedResourceResolvingLocalPath(bool bindBeforeVolume)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        // A custom compute resource still resolves the persistent volume's local store path, because
        // only containers short-circuit to the declared mount path. The env is spelled on the mount
        // here, so the binding itself carries none and both builder orders must still be rejected.
        var custom = builder.AddResource(new TestComputeResource("custom"));
        if (bindBeforeVolume)
        {
            custom.WithPersistentVolume(volume)
                .WithVolume("data", "/srv/data", env: "DATA_PATH");
        }
        else
        {
            custom.WithVolume("data", "/srv/data", env: "DATA_PATH")
                .WithPersistentVolume(volume);
        }

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));

        Assert.Contains("custom", exception.Message);
        Assert.Contains("DATA_PATH", exception.Message);
        Assert.Contains("Only project, executable, and container resources are supported", exception.Message);
    }

    [Fact]
    public async Task NameMatchBindingAllowsUnsupportedResourceWithoutLocalPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var kubernetes = builder.AddKubernetesEnvironment("env");
        var volume = kubernetes.AddPersistentVolume("data");

        // The env mount names an unrelated volume, so this binding never resolves the persistent
        // volume's store path and stays publish-only.
        builder.AddResource(new TestComputeResource("custom"))
            .WithVolume("scratch", "/srv/scratch", env: "SCRATCH_PATH")
            .WithPersistentVolume(volume);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(
        DistributedApplication app,
        CancellationToken cancellationToken);

    private sealed class TestComputeResource(string name) : Resource(name), IComputeResource, IResourceWithEnvironment;
}
