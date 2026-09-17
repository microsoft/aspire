// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Backchannel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

[Collection(ConsoleOutputCollection.Name)]
public class DoCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("do --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommand_WhenExtensionStepPromptIsCancelled_DoesNotDisplayError()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var displayedErrors = new List<string>();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp)
            {
                DisplayErrorCallback = displayedErrors.Add,
                PromptForStringCallback = (_, _, _, _, _, _) => throw new ExtensionOperationCanceledException("Pipeline step selection was canceled.")
            };
            options.ConfigurationCallback += config => config[KnownConfigNames.ExtensionDebugSessionId] = "test-session-id";
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("do");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Cancelled, exitCode);
        Assert.Empty(displayedErrors);
    }

    [Fact]
    public async Task DoCommandWithStepArgumentSucceeds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
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

                    // Simulate apphost running successfully and establishing a backchannel
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.True(options.NoLaunchProfile);

                        // Verify that the custom step is passed
                        Assert.Contains("--step", args);
                        Assert.Contains("my-custom-step", args);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do my-custom-step");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandWithDeployStepSucceeds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        // Verify that --step deploy is passed
                        Assert.Contains("--step", args);
                        Assert.Contains("deploy", args);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandWithPublishStepSucceeds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        // Verify that --step publish is passed
                        Assert.Contains("--step", args);
                        Assert.Contains("publish", args);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do publish");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandPassesOutputPathWhenSpecified()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        // Verify output path is included
                        Assert.Contains("--output-path", args);
                        
                        // Find the output path argument value
                        var outputPathIndex = Array.IndexOf(args, "--output-path");
                        Assert.True(outputPathIndex >= 0 && outputPathIndex < args.Length - 1);
                        var outputPath = args[outputPathIndex + 1];
                        Assert.EndsWith("test-output", outputPath);

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do my-step --output-path test-output");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandFailsWithInvalidProjectFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (1, false, null);
                    }
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do my-step --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode);
    }

    [Theory]
    [InlineData("do --list-steps --format=json", "do", null)]
    [InlineData("do --list-steps --format json deploy", "do", "deploy")]
    [InlineData("do --format json --list-steps deploy", "do", "deploy")]
    [InlineData("do deploy --list-steps --format json", "do", "deploy")]
    [InlineData("publish --list-steps --format=json --operation run", "publish", "publish")]
    [InlineData("deploy --list-steps --format=json --operation run", "deploy", "deploy")]
    public async Task PipelineCommandWithListStepsUsesInspectOperation(string commandLine, string commandName, string? expectedStep)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        var requestStopCalled = new TaskCompletionSource();
        var getPipelineStepsCalled = new TaskCompletionSource();
        string? requestedStep = "not-called";
        string[]? capturedArgs = null;
        var publishingActivitiesRequested = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        capturedArgs = args;
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = requestStopCalled,
                            GetPipelineStepsAsyncCalled = getPipelineStepsCalled,
                            GetPipelineStepsAsyncCallback = (step, ct) =>
                            {
                                requestedStep = step;
                                return Task.FromResult(new GetPipelineStepsResponse
                                {
                                    Steps =
                                    [
                                        new PipelineStepInfo
                                        {
                                            Name = "deploy",
                                            Description = "Deploy the application",
                                            DependsOn = ["publish"],
                                            Tags = ["deployment"],
                                            ResourceName = "api"
                                        }
                                    ]
                                });
                            },
                            GetPublishingActivitiesAsyncCallback = ct =>
                            {
                                publishingActivitiesRequested = true;
                                return AsyncEnumerable.Empty<PublishingActivity>();
                            }
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await requestStopCalled.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(commandLine);
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.True(getPipelineStepsCalled.Task.IsCompleted, "GetPipelineStepsAsync should have been called");
        Assert.True(requestStopCalled.Task.IsCompleted, "RequestStopAsync should have been called");
        Assert.Equal(expectedStep, requestedStep);
        Assert.False(publishingActivitiesRequested);
        Assert.NotNull(capturedArgs);
        Assert.Equal(
            commandName switch
            {
                "do" when expectedStep is not null => ["--operation", "inspect", "--step", expectedStep, "--operation", "inspect", "--list-steps", "true"],
                "do" => ["--operation", "inspect", "--operation", "inspect", "--list-steps", "true"],
                "publish" => ["--operation", "publish", "--step", "publish", "--operation", "run", "--operation", "inspect", "--list-steps", "true"],
                "deploy" => ["--operation", "publish", "--step", "deploy", "--operation", "run", "--operation", "inspect", "--list-steps", "true"],
                _ => throw new InvalidOperationException()
            },
            capturedArgs);
        var output = Assert.Single(interactionService.DisplayedRawText);
        Assert.Equal(ConsoleOutput.Standard, output.ConsoleOverride);
        using var document = JsonDocument.Parse(output.Text);
        var step = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal(5, step.EnumerateObject().Count());
        Assert.Equal("deploy", step.GetProperty("name").GetString());
        Assert.Equal("Deploy the application", step.GetProperty("description").GetString());
        Assert.Equal(["publish"], step.GetProperty("dependsOn").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["deployment"], step.GetProperty("tags").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("api", step.GetProperty("resourceName").GetString());
    }

    [Fact]
    public async Task DoCommandWithListStepsPropagatesAppHostExitCode()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, VersionHelper.GetDefaultTemplateVersion()),
                RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    backchannelCompletionSource?.SetResult(new TestAppHostBackchannel
                    {
                        RequestStopAsyncCalled = requestStopCalled,
                        GetPipelineStepsAsyncCallback = (step, ct) => Task.FromResult(new GetPipelineStepsResponse { Steps = [] })
                    });
                    await requestStopCalled.Task.DefaultTimeout();
                    return 42;
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(42, exitCode);
    }

    [Fact]
    public async Task DoCommandWithListStepsJsonDoesNotWriteTerminalProgressToStandardOutput()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();
        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, VersionHelper.GetDefaultTemplateVersion()),
                RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    backchannelCompletionSource?.SetResult(new TestAppHostBackchannel
                    {
                        RequestStopAsyncCalled = requestStopCalled,
                        GetPipelineStepsAsyncCallback = (step, ct) => Task.FromResult(new GetPipelineStepsResponse
                        {
                            Steps = [new PipelineStepInfo { Name = "deploy" }]
                        })
                    });
                    await requestStopCalled.Task.DefaultTimeout();
                    return 0;
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        using var standardOutput = new StringWriter();
        var originalStandardOutput = Console.Out;

        try
        {
            Console.SetOut(standardOutput);
            var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalStandardOutput);
        }

        Assert.Empty(standardOutput.ToString());
        var output = Assert.Single(interactionService.DisplayedRawText);
        JsonDocument.Parse(output.Text).Dispose();
    }

    [Fact]
    public async Task DoCommandWithListStepsAndStepArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = requestStopCalled
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await requestStopCalled.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act - step argument with --list-steps
        var result = command.Parse("do deploy --list-steps");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandWithListStepsDoesNotExecutePipeline()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var publishingActivitiesRequested = false;
        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = requestStopCalled,
                            GetPublishingActivitiesAsyncCallback = (ct) =>
                            {
                                publishingActivitiesRequested = true;
                                return AsyncEnumerable.Empty<PublishingActivity>();
                            }
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await requestStopCalled.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("do deploy --list-steps");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert - pipeline should NOT have been executed
        Assert.Equal(0, exitCode);
        Assert.False(publishingActivitiesRequested, "Publishing activities should not be requested when --list-steps is used");
    }

    [Fact]
    public async Task DoCommandWithListStepsReturnsBuildFailureWhenCurrentAppHostExitsBeforeBackchannel()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, VersionHelper.GetDefaultTemplateVersion()),
                RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    Task.FromResult(1)
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode);
    }

    [Fact]
    public async Task DoCommandWithListStepsReturnsIncompatibleWhenLegacyAppHostRejectsInspectOperation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, "13.6.0-preview.1.25310.2"),
                RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    options.StandardErrorCallback?.Invoke("Unhandled exception. Aspire.Hosting.DistributedApplicationException: Invalid operation specified. Valid operations are 'publish' or 'run'.");
                    return Task.FromResult(1);
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
    }

    [Fact]
    public async Task DoCommandWithListStepsReturnsIncompatibleWhenLegacyAppHostFailsBackchannelConnection()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostExit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, "13.6.0-preview.1.25310.2"),
                RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    options.StandardErrorCallback?.Invoke("Unhandled exception. Aspire.Hosting.DistributedApplicationException: Invalid operation specified. Valid operations are 'publish' or 'run'.");
                    backchannelCompletionSource!.SetException(
                        new FailedToConnectBackchannelConnection("The AppHost process exited unexpectedly with exit code 1", new InvalidOperationException()));
                    return appHostExit.Task;
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();
        appHostExit.SetResult(1);

        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
    }

    [Fact]
    public async Task DoCommandWithListStepsReturnsIncompatibleWithoutLaunchingLegacyAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runCalled = false;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, "13.5.0"),
                RunAsyncCallback = (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    runCalled = true;
                    return Task.FromResult(0);
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        Assert.False(runCalled);
    }

    [Fact]
    public async Task DoCommandWithListStepsStopsAppHostWhenCapabilityIsMissing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, VersionHelper.GetDefaultTemplateVersion()),
                RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    backchannelCompletionSource?.SetResult(new TestAppHostBackchannel
                    {
                        RequestStopAsyncCalled = requestStopCalled,
                        GetCapabilitiesAsyncCallback = _ => Task.FromResult<string[]>(["baseline.v2"])
                    });
                    await requestStopCalled.Task.DefaultTimeout();
                    return 0;
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do --list-steps --format json").InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        Assert.True(requestStopCalled.Task.IsCompleted);
    }

    [Fact]
    public async Task DoCommandListStepsDisplaysCustomSteps()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var requestStopCalled = new TaskCompletionSource();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = requestStopCalled,
                            GetPipelineStepsAsyncCallback = (step, ct) => Task.FromResult(new GetPipelineStepsResponse
                            {
                                Steps = [
                                    new() { Name = "parameter-prompt" },
                                    new() { Name = "provision-redis-infra", DependsOn = ["parameter-prompt"], Tags = ["provision-infra"] },
                                    new() { Name = "build-webapi", DependsOn = ["parameter-prompt"], Tags = ["build-compute"] },
                                    new() { Name = "deploy-webapi", DependsOn = ["provision-redis-infra", "build-webapi"], Tags = ["deploy-compute"] }
                                ]
                            })
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await requestStopCalled.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("do deploy --list-steps");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandWithHelpShowsListStepsOption()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("do --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandForwardsFormatWithoutListStepsToAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        string[]? capturedArgs = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.DotNetCliRunnerFactory = _ => new TestDotNetCliRunner
            {
                BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    (0, true, VersionHelper.GetDefaultTemplateVersion()),
                RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                {
                    capturedArgs = args;
                    var completed = new TaskCompletionSource();
                    backchannelCompletionSource?.SetResult(new TestAppHostBackchannel
                    {
                        RequestStopAsyncCalled = completed
                    });
                    await completed.Task.DefaultTimeout();
                    return 0;
                }
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse("do deploy --format yaml").InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.Equal(["--operation", "publish", "--step", "deploy", "--format", "yaml"], capturedArgs);
    }

    [Theory]
    [InlineData("do --list-steps --format")]
    [InlineData("do --list-steps --format=")]
    [InlineData("do --list-steps --format yaml")]
    public async Task DoCommandWithListStepsReturnsInvalidCommandForInvalidFormat(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interactionService = new TestInteractionService();

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var exitCode = await command.Parse(commandLine).InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Single(interactionService.DisplayedErrors, "The --format option requires either 'table' or 'json'.");
    }

    [Fact]
    public async Task DoCommandForwardsPipelineLogLevelAsLogLevelToAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        string[]? capturedArgs = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        capturedArgs = args;

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("do my-step --pipeline-log-level debug");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedArgs);
        var logLevelIndex = Array.IndexOf(capturedArgs, "--log-level");
        Assert.True(logLevelIndex >= 0, "Expected --log-level argument to be passed to AppHost");
        Assert.Equal("debug", capturedArgs[logLevelIndex + 1]);
        Assert.DoesNotContain("--pipeline-log-level", capturedArgs);
    }

    [Fact]
    public async Task DoCommandDoesNotForwardCliLogLevelToAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        string[]? capturedArgs = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,

                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, true, VersionHelper.GetDefaultTemplateVersion());
                    },

                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        capturedArgs = args;

                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("do my-step --log-level Warning");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedArgs);
        Assert.DoesNotContain("--log-level", capturedArgs);
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
    public const string Name = nameof(ConsoleOutputCollection);
}
