// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Terminals;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.Tests;

public abstract class ContainerReplCommandTestBase
{
    protected abstract IResourceBuilder<ContainerResource> AddContainer(IDistributedApplicationBuilder builder);

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, 1)]
    [InlineData(DistributedApplicationOperation.Publish, 0)]
    public void ReplCommandIsOnlyAddedInRunMode(DistributedApplicationOperation operation, int expectedCount)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var resource = AddContainer(builder);

        var commands = resource.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();
        Assert.Equal(expectedCount, commands.Length);
        if (expectedCount == 1)
        {
            Assert.Equal("repl", commands[0].Name);
            Assert.Equal("REPL", commands[0].DisplayName);
            Assert.Equal("WindowConsole", commands[0].IconName);
        }
    }

    [Theory]
    [InlineData(nameof(KnownResourceStates.Running), "container-id", ResourceCommandState.Enabled)]
    [InlineData(nameof(KnownResourceStates.Running), null, ResourceCommandState.Disabled)]
    [InlineData(nameof(KnownResourceStates.Running), "", ResourceCommandState.Disabled)]
    [InlineData(nameof(KnownResourceStates.Starting), "container-id", ResourceCommandState.Disabled)]
    [InlineData(nameof(KnownResourceStates.Exited), "container-id", ResourceCommandState.Disabled)]
    [InlineData(null, null, ResourceCommandState.Disabled)]
    public void ReplCommandRequiresRunningContainer(string? state, string? containerId, ResourceCommandState expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = AddContainer(builder);
        using var app = builder.Build();
        var command = Assert.Single(resource.Resource.Annotations.OfType<ResourceCommandAnnotation>());

        var result = command.UpdateState(new UpdateCommandStateContext
        {
            Services = app.Services,
            ResourceSnapshot = new CustomResourceSnapshot
            {
                ResourceType = "Container",
                State = state is null ? null : new ResourceStateSnapshot(state, null),
                Properties = containerId is null ? [] : [new("container.id", containerId)]
            }
        });

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(nameof(KnownResourceStates.Exited), "container-id", "The container is not running.")]
    [InlineData(nameof(KnownResourceStates.Running), null, "The container ID is not available yet.")]
    public async Task ReplCommandRechecksStateWhenExecuted(string state, string? containerId, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = AddContainer(builder);
        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
        {
            State = state,
            Properties = containerId is null ? [] : [new("container.id", containerId)]
        });
        var command = Assert.Single(resource.Resource.Annotations.OfType<ResourceCommandAnnotation>());

        var result = await command.ExecuteCommand(new ExecuteCommandContext
        {
            ResourceName = resource.Resource.Name,
            Services = app.Services,
            CancellationToken = TestContext.Current.CancellationToken,
            Arguments = new InteractionInputCollection([]),
            Logger = NullLogger.Instance
        });

        Assert.False(result.Success);
        Assert.Equal(message, result.Message);
    }

    [Fact]
    public async Task ReplCommandHonorsCancellation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = AddContainer(builder);
        using var app = builder.Build();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var command = Assert.Single(resource.Resource.Annotations.OfType<ResourceCommandAnnotation>());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteCommand(new ExecuteCommandContext
        {
            ResourceName = resource.Resource.Name,
            Services = app.Services,
            CancellationToken = cancellation.Token,
            Arguments = new InteractionInputCollection([]),
            Logger = NullLogger.Instance
        }));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("podman")]
    public void ExecOptionsForwardEnvironmentWithoutPuttingSecretsInArguments(string runtime)
    {
        var options = new TerminalLaunchOptions
        {
            Title = "REPL",
            Executable = "client",
            Arguments = ["--username", "user with spaces"],
            EnvironmentVariables = { ["PASSWORD"] = "quotes'\" $; spaces" }
        };

        var execOptions = ContainerReplCommand.CreateExecOptions(options, runtime, "current-container-id");

        Assert.Equal(runtime, execOptions.Executable);
        Assert.Equal("REPL", execOptions.Title);
        Assert.Equal(TerminalPlacement.Dock, execOptions.Placement);
        Assert.Equal(["exec", "-it", "--env", "PASSWORD", "current-container-id", "client", "--username", "user with spaces"], execOptions.Arguments);
        Assert.Collection(execOptions.EnvironmentVariables, variable =>
        {
            Assert.Equal("PASSWORD", variable.Key);
            Assert.Equal("quotes'\" $; spaces", variable.Value);
        });
    }

    protected static Task VerifyReplAsync(
        DistributedApplication app,
        ContainerResource resource,
        string title,
        string prompt,
        string input,
        string expectedOutput) =>
        VerifyReplAsync(app, resource, title, [(prompt, input)], expectedOutput);

    protected static async Task VerifyReplAsync(
        DistributedApplication app,
        ContainerResource resource,
        string title,
        (string Prompt, string Input)[] interactions,
        string expectedOutput)
    {
        using (var startup = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            await app.StartAsync(startup.Token);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Name, cancellation.Token);
        await using var terminal = await TerminalCommandTestHelpers.ExecuteTerminalCommandAsync(app, resource, "repl", cancellation.Token);
        Assert.Equal(title, terminal.Title);
        Assert.Equal(TerminalPlacement.Dock, terminal.Placement);
        try
        {
            foreach (var (prompt, input) in interactions)
            {
                await terminal.WaitForTextAsync(prompt, TimeSpan.FromSeconds(30), cancellation.Token);
                await terminal.SendTextAsync(input, cancellation.Token);
            }
            await terminal.WaitForTextAsync(expectedOutput, TimeSpan.FromSeconds(30), cancellation.Token);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"REPL did not produce the expected output. Terminal screen:{Environment.NewLine}{terminal.GetScreenText()}", exception);
        }
    }
}
