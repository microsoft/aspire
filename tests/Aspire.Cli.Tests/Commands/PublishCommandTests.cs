// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Commands;

public class PublishCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task PublishCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("publish --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task PublishCommandListsPipelineInputsAsJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var publishingActivitiesRequested = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();
            options.InteractionServiceFactory = (sp) => interactionService;

            options.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) => (0, true, VersionHelper.GetDefaultTemplateVersion()),
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.Contains("--step", args);
                        Assert.Contains("publish", args);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetPipelineInputsAsyncCallback = (step, cancellationToken) =>
                            {
                                Assert.Equal("publish", step);
                                return Task.FromResult(new GetPipelineInputsResponse
                                {
                                    Inputs = [new PipelineInput { Name = "imageTag", ConfigurationKey = "Parameters:imageTag", InputType = "Text" }]
                                });
                            },
                            GetPublishingActivitiesAsyncCallback = cancellationToken =>
                            {
                                publishingActivitiesRequested = true;
                                return AsyncEnumerable.Empty<PublishingActivity>();
                            }
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("publish --list-inputs --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(publishingActivitiesRequested);
        var output = Assert.Single(interactionService.DisplayedRawText).Text;
        Assert.Contains("\"operation\": \"publish\"", output);
        Assert.Contains("\"step\": \"publish\"", output);
        Assert.Contains("\"name\": \"imageTag\"", output);
    }

    [Fact]
    public async Task PublishCommandListsPipelineResourcesAsJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var publishingActivitiesRequested = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();
            options.InteractionServiceFactory = (sp) => interactionService;

            options.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) => (0, true, VersionHelper.GetDefaultTemplateVersion()),
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.Contains("--step", args);
                        Assert.Contains("publish", args);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetPipelineResourcesAsyncCallback = (includeHidden, cancellationToken) =>
                            {
                                Assert.False(includeHidden);
                                return Task.FromResult(new GetPipelineResourcesResponse
                                {
                                    Resources =
                                    [
                                        new ResourceSnapshot
                                        {
                                            Name = "web",
                                            DisplayName = "web",
                                            ResourceType = "Project"
                                        }
                                    ]
                                });
                            },
                            GetPublishingActivitiesAsyncCallback = cancellationToken =>
                            {
                                publishingActivitiesRequested = true;
                                return AsyncEnumerable.Empty<PublishingActivity>();
                            }
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("publish --list-resources --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(publishingActivitiesRequested);
        var output = Assert.Single(interactionService.DisplayedRawText).Text;
        Assert.Contains("\"resources\":", output);
        Assert.Contains("\"name\": \"web\"", output);
        Assert.Contains("\"resourceType\": \"Project\"", output);
    }

    [Fact]
    public async Task PublishCommandFailsWithInvalidProjectFile()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                {
                    return (1, false, null); // Simulate failure to retrieve app host information
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("publish --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task PublishCommandFailsWhenAppHostIsNotCompatible()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                {
                    return (0, false, "9.0.0"); // Simulate an incompatible app host
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("publish --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task PublishCommandFailsWhenAppHostBuildFails()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();
                runner.BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) =>
                {
                    return 1; // Simulate a build failure
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("publish --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task PublishCommandFailsWhenAppHostCrashesBeforeBackchannelEstablished()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                // Simulate a successful build
                runner.BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0;

                // Simulate apphost starting but crashing before backchannel is established
                runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    // Simulate a delay to mimic apphost starting
                    await Task.Delay(100, cancellationToken);

                    // Simulate apphost crash by completing the backchannel with an exception
                    backchannelCompletionSource?.SetException(new InvalidOperationException("AppHost process has exited unexpectedly. Use --debug to see more details."));

                    return 1; // Non-zero exit code to indicate failure
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("publish --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task PublishCommandSucceedsEndToEnd()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner();

                // Simulate a successful build
                runner.BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0;

                // Simulate a successful app host information retrieval
                runner.GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                {
                    return (0, true, VersionHelper.GetDefaultTemplateVersion()); // Compatible app host with backchannel support
                };

                // Simulate apphost running successfully and establishing a backchannel
                runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    Assert.True(options.NoLaunchProfile);

                    if (args.Any(a => a == "inspect"))
                    {
                        var inspectModeCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel();
                        backchannel.RequestStopAsyncCalled = inspectModeCompleted;
                        backchannelCompletionSource?.SetResult(backchannel);
                        await inspectModeCompleted.Task.DefaultTimeout();
                        return 0;
                    }
                    else
                    {
                        var publishModeCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel();
                        backchannel.RequestStopAsyncCalled = publishModeCompleted;
                        backchannelCompletionSource?.SetResult(backchannel);
                        await publishModeCompleted.Task.DefaultTimeout();
                        return 0; // Simulate successful run
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestPublishCommandPrompter(interactionService);
                return prompter;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("publish");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode); // Ensure the command succeeds
    }
}

internal sealed class TestPublishCommandPrompter(IInteractionService interactionService) : PublishCommandPrompter(interactionService)
{
    public Func<IEnumerable<string>, string>? PromptForPublisherCallback { get; set; }

    public override Task<string> PromptForPublisherAsync(IEnumerable<string> publishers, CancellationToken cancellationToken)
    {
        return PromptForPublisherCallback switch
        {
            { } callback => Task.FromResult(callback(publishers)),
            _ => Task.FromResult(publishers.First()) // Default to the first publisher if no callback is provided.
        };
    }
}
