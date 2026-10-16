// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001 // ProjectLaunchDefaultsAnnotation is experimental.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Resources;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ResourceCommandAnnotationTests
{
    [Fact]
    public void WaitingStates_ContainsOnlyStartupBlockers()
    {
        Assert.Equal(
            new[] { KnownResourceStates.Waiting, KnownResourceStates.UnresolvedParameters },
            KnownResourceStates.WaitingStates);
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
    [InlineData("start", "UnresolvedParameters", ResourceCommandState.Disabled)]
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
    [InlineData("stop", "UnresolvedParameters", ResourceCommandState.Hidden)]
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
    [InlineData("restart", "UnresolvedParameters", ResourceCommandState.Disabled)]
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
    [InlineData("rebuild", "UnresolvedParameters", ResourceCommandState.Enabled)]
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
    [InlineData(nameof(KnownResourceStates.Waiting), true, false)]
    [InlineData(nameof(KnownResourceStates.Waiting), true, true)]
    [InlineData(nameof(KnownResourceStates.UnresolvedParameters), false, false)]
    [InlineData(nameof(KnownResourceStates.UnresolvedParameters), false, true)]
    [InlineData(nameof(KnownResourceStates.UnresolvedParameters), true, false)]
    [InlineData(nameof(KnownResourceStates.UnresolvedParameters), true, true)]
    public async Task RebuildCommand_PreservesStartupBlockedReplicas(string waitingState, bool waitForDependencies, bool hasRunningReplica)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = builder.AddParameter("value");
        var project = builder.AddProject<Projects.ServiceA>("project", options => options.ExcludeLaunchProfile = true)
            .WithEnvironment("VALUE", parameter);
        if (waitForDependencies)
        {
            project.WaitFor(builder.AddContainer("dependency", "image"));
        }

        const string blockedName = "project-blocked";
        const string runningName = "project-running";
        var instances = new List<DcpInstance> { new(blockedName, "blocked", 0) };
        if (hasRunningReplica)
        {
            instances.Add(new(runningName, "running", 1));
        }
        project.Resource.Annotations.Add(new DcpInstancesAnnotation([.. instances]));
        project.Resource.AddLifeCycleCommands();
        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var executor = new TestDcpExecutor();
        executor.AddResource(project.Resource, blockedName);
        if (hasRunningReplica)
        {
            executor.AddResource(project.Resource, runningName);
        }
        executor.AddResource(rebuilder, rebuilder.Name);
        builder.Services.AddSingleton<IDcpExecutor>(executor);

        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var properties = new List<ResourcePropertySnapshot>();
        if (waitingState == KnownResourceStates.UnresolvedParameters)
        {
            properties.Add(new(KnownProperties.Resource.UnresolvedParameters, new[] { parameter.Resource.Name }));
        }
        if (waitForDependencies)
        {
            properties.Add(new(KnownProperties.Resource.WaitingFor, new[] { "dependency" }));
        }
        await notifications.PublishUpdateAsync(project.Resource, blockedName, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot(waitingState,
                waitingState == KnownResourceStates.UnresolvedParameters ? KnownResourceStateStyles.Warn : KnownResourceStateStyles.Info),
            Properties = [.. properties]
        });
        Assert.True(notifications.TryGetCurrentState(blockedName, out var initialBlockedEvent));

        if (hasRunningReplica)
        {
            await notifications.PublishUpdateAsync(project.Resource, runningName, snapshot => snapshot with
            {
                State = KnownResourceStates.Running
            });
        }

        executor.StartResourceCallback = async (reference, _) =>
        {
            Assert.True(notifications.TryGetCurrentState(blockedName, out var blockedEvent));
            Assert.Equal(initialBlockedEvent.Snapshot.State, blockedEvent.Snapshot.State);
            Assert.Equal(initialBlockedEvent.Snapshot.Properties, blockedEvent.Snapshot.Properties);

            if (ReferenceEquals(reference.ModelResource, rebuilder))
            {
                Assert.Equal(hasRunningReplica ? [runningName] : Array.Empty<string>(), executor.StoppedResources);
                if (hasRunningReplica)
                {
                    Assert.True(notifications.TryGetCurrentState(runningName, out var buildingEvent));
                    Assert.Equal(KnownResourceStates.Building, buildingEvent.Snapshot.State?.Text);
                }
                await notifications.PublishUpdateAsync(rebuilder, snapshot => snapshot with
                {
                    State = KnownResourceStates.Finished,
                    ExitCode = 0
                });
            }
            else
            {
                Assert.Equal(runningName, reference.DcpResourceName);
                await notifications.PublishUpdateAsync(project.Resource, runningName, snapshot => snapshot with
                {
                    State = KnownResourceStates.Running
                });
            }
        };

        var command = Assert.Single(project.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            annotation => annotation.Name == KnownResourceCommands.RebuildCommand);
        var result = await command.ExecuteCommand(new ExecuteCommandContext
        {
            ResourceName = hasRunningReplica ? runningName : blockedName,
            Services = app.Services,
            CancellationToken = TestContext.Current.CancellationToken,
            Arguments = new InteractionInputCollection([]),
            Logger = NullLogger.Instance
        }).DefaultTimeout();

        Assert.True(result.Success);
        Assert.Equal(hasRunningReplica ? [rebuilder.Name, runningName] : [rebuilder.Name], executor.StartedResources);
        Assert.True(notifications.TryGetCurrentState(blockedName, out var finalBlockedEvent));
        Assert.Equal(initialBlockedEvent.Snapshot.State, finalBlockedEvent.Snapshot.State);
        Assert.Equal(initialBlockedEvent.Snapshot.Properties, finalBlockedEvent.Snapshot.Properties);
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
