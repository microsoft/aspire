// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchDefaultsAnnotation is experimental.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceCommandAnnotationTests
{
    [Theory]
    [InlineData("Pending", "start", ResourceCommandState.Disabled)]
    [InlineData("Pending", "stop", ResourceCommandState.Disabled)]
    [InlineData("Pending", "restart", ResourceCommandState.Disabled)]
    [InlineData("Pending", "reset-volumes", ResourceCommandState.Disabled)]
    [InlineData("Running", "start", ResourceCommandState.Disabled)]
    [InlineData("Running", "stop", ResourceCommandState.Disabled)]
    [InlineData("Running", "restart", ResourceCommandState.Disabled)]
    [InlineData("Running", "reset-volumes", ResourceCommandState.Disabled)]
    [InlineData("Failed", "start", ResourceCommandState.Hidden)]
    [InlineData("Failed", "stop", ResourceCommandState.Enabled)]
    [InlineData("Failed", "restart", ResourceCommandState.Enabled)]
    [InlineData("Failed", "reset-volumes", ResourceCommandState.Enabled)]
    [InlineData("Succeeded", "start", ResourceCommandState.Hidden)]
    [InlineData("Succeeded", "stop", ResourceCommandState.Enabled)]
    [InlineData("Succeeded", "restart", ResourceCommandState.Enabled)]
    [InlineData("Succeeded", "reset-volumes", ResourceCommandState.Enabled)]
    public void ResetVolumesCommand_DisablesLifecycleOnlyDuringActiveOperation(string resetState, string commandName, ResourceCommandState expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("database", "image").WithVolume("data", "/data").Resource;
        resource.AddLifeCycleCommands();
        var command = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(c => c.Name == commandName);
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(expected, command.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "container",
                State = KnownResourceStates.Running,
                Properties = [new(KnownProperties.Container.VolumeResetState, resetState)]
            },
            Services = services
        }));
    }

    [Theory]
    [InlineData("container", true)]
    [InlineData("container", false)]
    [InlineData("executable", true)]
    [InlineData("executable", false)]
    [InlineData("project", true)]
    [InlineData("project", false)]
    public void ResetVolumesCommand_RegisteredOnlyForVolumesAcrossComputeTypes(string resourceType, bool hasVolume)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        IResource resource = resourceType switch
        {
            "container" => builder.AddContainer("service", "image").Resource,
            "executable" => builder.AddExecutable("service", "command", ".").Resource,
            _ => builder.AddProject<Projects.ServiceA>("service", launchProfileName: null).Resource
        };
        resource.Annotations.Add(new ContainerMountAnnotation(hasVolume ? "data" : Path.GetFullPath("data"), "/data", hasVolume ? ContainerMountType.Volume : ContainerMountType.BindMount, isReadOnly: false));
        resource.AddLifeCycleCommands();

        Assert.Equal(hasVolume, resource.Annotations.OfType<ResourceCommandAnnotation>().Any(c => c.Name == KnownResourceCommands.ResetVolumesCommand));
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Enabled)]
    [InlineData("Exited", ResourceCommandState.Enabled)]
    [InlineData("Starting", ResourceCommandState.Disabled)]
    [InlineData("Stopping", ResourceCommandState.Disabled)]
    [InlineData("Waiting", ResourceCommandState.Disabled)]
    [InlineData("Building", ResourceCommandState.Disabled)]
    [InlineData("RuntimeUnhealthy", ResourceCommandState.Disabled)]
    [InlineData(null, ResourceCommandState.Disabled)]
    public void ResetVolumesCommand_StateAndConfirmation(string? state, ResourceCommandState expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("database", "image").WithVolume("data", "/data").Resource;
        resource.AddLifeCycleCommands();
        var command = resource.Annotations.OfType<ResourceCommandAnnotation>().Single(c => c.Name == KnownResourceCommands.ResetVolumesCommand);
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.Equal(CommandStrings.ResetVolumesConfirmation, command.ConfirmationMessage);
        Assert.Equal(expected, command.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot { ResourceType = "container", Properties = [], State = state },
            Services = services
        }));
    }

    [Fact]
    public void ResetVolumesCommand_NotAddedForBindMounts()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainer("database", "image").WithBindMount(Path.GetFullPath("data"), "/data").Resource;
        resource.AddLifeCycleCommands();

        Assert.Equal(["start", "stop", "restart"], resource.Annotations.OfType<ResourceCommandAnnotation>().Select(c => c.Name));
    }

    [Theory]
    [InlineData("start", "Starting", ResourceCommandState.Disabled)]
    [InlineData("start", "Stopping", ResourceCommandState.Hidden)]
    [InlineData("start", "Running", ResourceCommandState.Hidden)]
    [InlineData("start", "Exited", ResourceCommandState.Enabled)]
    [InlineData("start", "Finished", ResourceCommandState.Enabled)]
    [InlineData("start", "FailedToStart", ResourceCommandState.Enabled)]
    [InlineData("start", "Unknown", ResourceCommandState.Enabled)]
    [InlineData("start", "Waiting", ResourceCommandState.Enabled)]
    [InlineData("start", "Building", ResourceCommandState.Disabled)]
    [InlineData("start", "RuntimeUnhealthy", ResourceCommandState.Disabled)]
    [InlineData("start", "", ResourceCommandState.Disabled)]
    [InlineData("start", null, ResourceCommandState.Disabled)]
    [InlineData("stop", "Starting", ResourceCommandState.Hidden)]
    [InlineData("stop", "Stopping", ResourceCommandState.Disabled)]
    [InlineData("stop", "Running", ResourceCommandState.Enabled)]
    [InlineData("stop", "Exited", ResourceCommandState.Hidden)]
    [InlineData("stop", "Finished", ResourceCommandState.Hidden)]
    [InlineData("stop", "FailedToStart", ResourceCommandState.Hidden)]
    [InlineData("stop", "Unknown", ResourceCommandState.Hidden)]
    [InlineData("stop", "Waiting", ResourceCommandState.Hidden)]
    [InlineData("stop", "Building", ResourceCommandState.Hidden)]
    [InlineData("stop", "RuntimeUnhealthy", ResourceCommandState.Hidden)]
    [InlineData("stop", "", ResourceCommandState.Hidden)]
    [InlineData("stop", null, ResourceCommandState.Hidden)]
    [InlineData("restart", "Starting", ResourceCommandState.Disabled)]
    [InlineData("restart", "Stopping", ResourceCommandState.Disabled)]
    [InlineData("restart", "Running", ResourceCommandState.Enabled)]
    [InlineData("restart", "Exited", ResourceCommandState.Disabled)]
    [InlineData("restart", "Finished", ResourceCommandState.Disabled)]
    [InlineData("restart", "FailedToStart", ResourceCommandState.Disabled)]
    [InlineData("restart", "Unknown", ResourceCommandState.Disabled)]
    [InlineData("restart", "Waiting", ResourceCommandState.Disabled)]
    [InlineData("restart", "Building", ResourceCommandState.Disabled)]
    [InlineData("restart", "RuntimeUnhealthy", ResourceCommandState.Disabled)]
    [InlineData("restart", "", ResourceCommandState.Disabled)]
    [InlineData("restart", null, ResourceCommandState.Disabled)]
    public void LifeCycleCommands_CommandState(string commandName, string? resourceState, ResourceCommandState commandState)
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("name", "image");
        resourceBuilder.Resource.AddLifeCycleCommands();

        var startCommand = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == commandName);

        // Act
        var state = startCommand.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                Properties = [],
                ResourceType = "test",
                State = resourceState
            },
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // Assert
        Assert.Equal(commandState, state);
    }

    [Fact]
    public void RestartCommand_ContainerResource_HasGenericDescription()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var resourceBuilder = builder.AddContainer("name", "image");
        resourceBuilder.Resource.AddLifeCycleCommands();

        // Act
        var restartCommand = resourceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        // Assert - Container resources should have the generic description
        Assert.Equal(CommandStrings.RestartDescription, restartCommand.DisplayDescription);
    }

    [Fact]
    public void RestartCommand_ProjectResource_HasDetailedDescription()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var projectResource = new ProjectResource("testproject");
        projectResource.AddLifeCycleCommands();

        // Act
        var restartCommand = projectResource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        // Assert - Project resources should have the detailed description mentioning source code is not recompiled
        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
    }

    [Fact]
    public void RestartCommand_CSharpAppResource_HasDetailedDescription()
    {
        // Arrange
        var builder = DistributedApplication.CreateBuilder();
        var csharpAppResource = new CSharpAppResource("testapp");
        csharpAppResource.AddLifeCycleCommands();

        // Act
        var restartCommand = csharpAppResource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RestartCommand);

        // Assert - Single file C# app resources should have the detailed description mentioning source code is not recompiled
        Assert.Equal(CommandStrings.RestartProjectDescription, restartCommand.DisplayDescription);
    }

    [Theory]
    [InlineData("rebuild", "Starting", ResourceCommandState.Disabled)]
    [InlineData("rebuild", "Stopping", ResourceCommandState.Disabled)]
    [InlineData("rebuild", "Running", ResourceCommandState.Enabled)]
    [InlineData("rebuild", "Exited", ResourceCommandState.Enabled)]
    [InlineData("rebuild", "Finished", ResourceCommandState.Enabled)]
    [InlineData("rebuild", "FailedToStart", ResourceCommandState.Enabled)]
    [InlineData("rebuild", "Unknown", ResourceCommandState.Disabled)]
    [InlineData("rebuild", "Waiting", ResourceCommandState.Enabled)]
    [InlineData("rebuild", "RuntimeUnhealthy", ResourceCommandState.Disabled)]
    [InlineData("rebuild", "Building", ResourceCommandState.Disabled)]
    [InlineData("rebuild", "", ResourceCommandState.Disabled)]
    [InlineData("rebuild", null, ResourceCommandState.Disabled)]
    public void RebuildCommand_CommandState(string commandName, string? resourceState, ResourceCommandState commandState)
    {
        var projectResource = new ProjectResource("testproject");
        projectResource.AddLifeCycleCommands();

        var rebuildCommand = projectResource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == commandName);

        var state = rebuildCommand.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot
            {
                Properties = [],
                ResourceType = "test",
                State = resourceState
            },
            Services = new ServiceCollection().BuildServiceProvider()
        });

        Assert.Equal(commandState, state);
    }

    [Fact]
    public void RebuildCommand_OnlyAddedToProjectResources()
    {
        var builder = DistributedApplication.CreateBuilder();

        var containerResource = builder.AddContainer("container", "image");
        containerResource.Resource.AddLifeCycleCommands();

        var projectResource = new ProjectResource("testproject");
        projectResource.AddLifeCycleCommands();

        Assert.DoesNotContain(containerResource.Resource.Annotations.OfType<ResourceCommandAnnotation>(), a => a.Name == KnownResourceCommands.RebuildCommand);
        Assert.Contains(projectResource.Annotations.OfType<ResourceCommandAnnotation>(), a => a.Name == KnownResourceCommands.RebuildCommand);
    }

    [Fact]
    public void RebuildCommand_ProjectResource_HasDescription()
    {
        var projectResource = new ProjectResource("testproject");
        projectResource.AddLifeCycleCommands();

        var rebuildCommand = projectResource.Annotations.OfType<ResourceCommandAnnotation>().Single(a => a.Name == KnownResourceCommands.RebuildCommand);

        Assert.Equal(CommandStrings.RebuildName, rebuildCommand.DisplayName);
        Assert.Equal(CommandStrings.RebuildDescription, rebuildCommand.DisplayDescription);
    }

    [Theory]
    [InlineData("aspire-dashboard", false)]
    [InlineData("project", true)]
    public void ProjectCommandCancellationToken_UsesAppHostLifetimeForDashboard(string resourceName, bool expectedCanceled)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var app = builder.Build();
        using var commandCancellation = new CancellationTokenSource();
        commandCancellation.Cancel();

        var projectResource = new ProjectResource(resourceName);
        projectResource.Annotations.Add(new ProjectLaunchDefaultsAnnotation());
        var context = new ExecuteCommandContext
        {
            ResourceName = resourceName,
            Services = app.Services,
            CancellationToken = commandCancellation.Token,
            Arguments = new InteractionInputCollection([]),
            Logger = NullLogger.Instance
        };

        var cancellationToken = CommandsConfigurationExtensions.GetCommandCancellationToken(context, projectResource);

        Assert.Equal(expectedCanceled, cancellationToken.IsCancellationRequested);
        if (!expectedCanceled)
        {
            Assert.Equal(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping, cancellationToken);
        }
    }

    [Theory]
    [InlineData("Exited", null, false)]
    [InlineData("Exited", 0, true)]
    [InlineData("Finished", 1, true)]
    [InlineData("FailedToStart", null, true)]
    [InlineData("Running", null, false)]
    public void IsRebuildComplete_RequiresExitCodeForTerminalState(string state, int? exitCode, bool expected)
    {
        var resource = new ProjectResource("project");
        var resourceEvent = new ResourceEvent(resource, "project-rebuilder", new CustomResourceSnapshot
        {
            ResourceType = KnownResourceTypes.Executable,
            State = new ResourceStateSnapshot(state, null),
            ExitCode = exitCode,
            Properties = []
        });

        Assert.Equal(expected, CommandsConfigurationExtensions.IsRebuildComplete(resourceEvent, "project-rebuilder"));
    }
}
