// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DeployCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DeployCommandWithHelpArgumentReturnsZero()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("deploy --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DeployCommandFailsWithInvalidProjectFile()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (1, false, null); // Simulate failure to retrieve app host information
                    }
                };
                return runner;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandFailsWhenAppHostIsNotCompatible()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, false, "9.0.0"); // Simulate an incompatible app host
                    }
                };
                return runner;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.AppHostIncompatible, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandFailsWhenAppHostBuildFails()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) =>
                    {
                        return 1; // Simulate a build failure
                    }
                };
                return runner;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.FailedToBuildArtifacts, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandSucceedsWithoutOutputPath()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    // Simulate a successful build
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    // Simulate a successful app host information retrieval
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion()); // Compatible app host with backchannel support
                    },

                    // Simulate apphost running successfully and establishing a backchannel
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.True(options.NoLaunchProfile);

                        // Verify that --output-path is NOT included when not specified
                        Assert.DoesNotContain("--output-path", args);

                        // Verify that --step deploy is passed by default
                        Assert.Contains("--step", args);
                        Assert.Contains("deploy", args);

                        var deployModeCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = deployModeCompleted
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await deployModeCompleted.Task.DefaultTimeout();
                        return 0; // Simulate successful run
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestDeployCommandPrompter(interactionService);
                return prompter;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode); // Ensure the command succeeds
    }

    [Fact]
    public async Task DeployCommandSucceedsEndToEnd()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    // Simulate a successful build
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    // Simulate a successful app host information retrieval
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion()); // Compatible app host with backchannel support
                    },

                    // Simulate apphost running successfully and establishing a backchannel
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.True(options.NoLaunchProfile);

                        // Verify the complete set of expected arguments for deploy command
                        Assert.Contains("--operation", args);
                        Assert.Contains("publish", args);

                        // Verify that --step deploy is passed by default
                        Assert.Contains("--step", args);
                        Assert.Contains("deploy", args);

                        var deployModeCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = deployModeCompleted
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await deployModeCompleted.Task.DefaultTimeout();
                        return 0; // Simulate successful run
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestDeployCommandPrompter(interactionService);
                return prompter;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode); // Ensure the command succeeds
    }

    [Fact]
    public async Task DeployCommandIncludesDeployFlagInArguments()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Use a cross-platform path for testing
        var testOutputPath = Path.Combine(Path.GetTempPath(), "test");

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    // Simulate a successful build
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    // Simulate a successful app host information retrieval
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                        {
                            return (0, true, VersionHelper.GetDefaultTemplateVersion());
                        },

                    // Simulate apphost running and verify --step deploy flag is passed
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                        {
                            Assert.Contains("--operation", args);
                            Assert.Contains("publish", args);
                            // When output path is explicitly provided, it should be included
                            Assert.Contains("--output-path", args);
                            Assert.Contains(testOutputPath, args);
                            // Verify that --step deploy is passed by default
                            Assert.Contains("--step", args);
                            Assert.Contains("deploy", args);

                            var deployModeCompleted = new TaskCompletionSource();
                            var backchannel = new TestAppHostBackchannel
                            {
                                RequestStopAsyncCalled = deployModeCompleted
                            };
                            backchannelCompletionSource?.SetResult(backchannel);
                            await deployModeCompleted.Task.DefaultTimeout();
                            return 0;
                        }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestDeployCommandPrompter(interactionService);
                return prompter;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse($"deploy --output-path {testOutputPath}");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DeployCommandReturnsNonZeroExitCodeWhenDeploymentFails()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    // Simulate a successful build
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    // Simulate a successful app host information retrieval
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion()); // Compatible app host with backchannel support
                    },

                    // Simulate apphost running but deployment fails
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var deployModeCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = deployModeCompleted,
                            GetPublishingActivitiesAsyncCallback = GetFailedDeploymentActivities
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await deployModeCompleted.Task.DefaultTimeout();
                        return 0; // AppHost exits with 0 even though deployment failed
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                var prompter = new TestDeployCommandPrompter(interactionService);
                return prompter;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.FailedToBuildArtifacts, exitCode); // Ensure the command returns a non-zero exit code

        static async IAsyncEnumerable<PublishingActivity> GetFailedDeploymentActivities([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Simulate a deployment step starting
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Step,
                Data = new PublishingActivityData
                {
                    Id = "deploy-step",
                    StatusText = "Deploying Azure resources",
                    CompletionState = CompletionStates.InProgress,
                    StepId = null
                }
            };

            // Simulate a task that fails
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "deploy-postgres",
                    StatusText = "Deploying postgres: 0%",
                    CompletionState = CompletionStates.InProgress,
                    StepId = "deploy-step"
                }
            };

            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "deploy-postgres",
                    StatusText = "Deploying postgres failed",
                    CompletionMessage = "Failed to deploy Azure resources",
                    CompletionState = CompletionStates.CompletedWithError,
                    StepId = "deploy-step"
                }
            };

            // Simulate the step completing with error
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Step,
                Data = new PublishingActivityData
                {
                    Id = "deploy-step",
                    StatusText = "Failed to deploy Azure resources",
                    CompletionState = CompletionStates.CompletedWithError,
                    StepId = null
                }
            };

            // Simulate publish complete with error
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.PublishComplete,
                Data = new PublishingActivityData
                {
                    Id = "publish-complete",
                    StatusText = "Deployment completed with errors",
                    CompletionState = CompletionStates.CompletedWithError,
                    StepId = null
                }
            };
        }
    }
}

internal sealed class TestDeployCommandPrompter(IInteractionService interactionService) : PublishCommandPrompter(interactionService)
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
