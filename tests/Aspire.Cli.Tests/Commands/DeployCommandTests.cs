// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DeployCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DeployCommandWithHelpArgumentReturnsZero()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("deploy --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DeployCommandInExtensionForwardsResolvedAspireHome()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var expectedAspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".home", ".aspire");
        DebugSessionOptions? capturedOptions = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp =>
            {
                var interactionService = new TestExtensionInteractionService(sp);
                interactionService.StartDebugSessionCallback = (_, _, _, debugSessionOptions) =>
                {
                    capturedOptions = debugSessionOptions;
                };
                return interactionService;
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
        Assert.NotNull(capturedOptions);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [KnownConfigNames.AspireHome] = expectedAspireHome
            },
            capturedOptions.EnvironmentVariables);
    }

    [Theory]
    [InlineData("deploy", "deploy", null, false, "default-discovery")]
    [InlineData("deploy", "deploy", null, true, "explicit-cli")]
    [InlineData("publish", "publish", null, false, "default-discovery")]
    [InlineData("publish", "publish", null, true, "explicit-cli")]
    [InlineData("do", "do deploy", "deploy", false, "default-discovery")]
    [InlineData("do", "do deploy", "deploy", true, "explicit-cli")]
    public async Task PipelineCommands_WhenDelegatingToExtension_CarryAppHostSelectionOrigin(
        string commandName,
        string commandText,
        string? expectedCommandArg,
        bool explicitAppHost,
        string expectedOrigin)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var expectedAspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".home", ".aspire");

        string? workingDirectory = null;
        string? projectFile = null;
        bool? debug = null;
        DebugSessionOptions? capturedOptions = null;

        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (passedProjectFile, _, _, _) =>
            {
                var selectedProjectFile = passedProjectFile ?? appHostFile;
                return Task.FromResult(new AppHostProjectSearchResult(selectedProjectFile, [selectedProjectFile]));
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp =>
            {
                var interactionService = new TestExtensionInteractionService(sp);
                interactionService.StartDebugSessionCallback = (capturedWorkingDirectory, capturedProjectFile, capturedDebug, debugSessionOptions) =>
                {
                    workingDirectory = capturedWorkingDirectory;
                    projectFile = capturedProjectFile;
                    debug = capturedDebug;
                    capturedOptions = debugSessionOptions;
                };
                return interactionService;
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var invocation = explicitAppHost ? $"{commandText} --apphost \"{appHostFile.FullName}\"" : commandText;

        var result = command.Parse(invocation);
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(workspace.WorkspaceRoot.FullName, workingDirectory);
        Assert.Equal(appHostFile.FullName, projectFile);
        Assert.True(debug);
        Assert.NotNull(capturedOptions);
        Assert.Equal(commandName, capturedOptions.Command);
        if (expectedCommandArg is null)
        {
            Assert.Null(capturedOptions.Args);
        }
        else
        {
            Assert.NotNull(capturedOptions.Args);
            Assert.Equal([expectedCommandArg], capturedOptions.Args);
        }
        Assert.Equal(
            new Dictionary<string, string>
            {
                [KnownConfigNames.AspireHome] = expectedAspireHome
            },
            capturedOptions.EnvironmentVariables);
        Assert.Equal(expectedOrigin, capturedOptions.AppHostSelectionOrigin);
    }

    [Fact]
    public async Task DeployCommandFailsWithInvalidProjectFile()
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
                        return (1, false, null); // Simulate failure to retrieve app host information
                    }
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToFindProject, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandFailsWhenAppHostIsNotCompatible()
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
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) =>
                    {
                        return (0, false, "9.0.0"); // Simulate an incompatible app host
                    }
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandFailsWhenAppHostBuildFails()
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
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) =>
                    {
                        return 1; // Simulate a build failure
                    }
                };
                return runner;
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy --apphost valid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode); // Ensure the command fails
    }

    [Fact]
    public async Task DeployCommandSucceedsWithoutOutputPath()
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

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(0, exitCode); // Ensure the command succeeds
    }

    [Fact]
    public async Task DeployCommandAppliesPipelineParameterArguments()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        TestAppHostBackchannel? capturedBackchannel = null;

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) => (0, true, VersionHelper.GetDefaultTemplateVersion()),
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var completed = new TaskCompletionSource();
                        capturedBackchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetCapabilitiesAsyncCallback = cancellationToken => Task.FromResult(new[] { "baseline.v2", "pipeline-steps.v1", "pipeline-steps.v2", "pipeline-inputs.v1" }),
                            GetPipelineInputsAsyncCallback = (step, cancellationToken) =>
                            {
                                Assert.Equal("deploy", step);
                                return Task.FromResult(new GetPipelineInputsResponse
                                {
                                    Inputs =
                                    [
                                        new PipelineInput { Name = "databasePassword", InputType = "SecretText", Required = true },
                                        new PipelineInput { Name = "replicas", InputType = "Number" },
                                        new PipelineInput { Name = "enableFeature", InputType = "Boolean" }
                                    ]
                                });
                            }
                        };
                        backchannelCompletionSource?.SetResult(capturedBackchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("deploy --database-password s3cr3t --replicas 3 --enable-feature");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.NotNull(capturedBackchannel?.AppliedPipelineParameterValues);
        Assert.Equal("s3cr3t", capturedBackchannel.AppliedPipelineParameterValues["databasePassword"]);
        Assert.Equal("3", capturedBackchannel.AppliedPipelineParameterValues["replicas"]);
        Assert.Equal("true", capturedBackchannel.AppliedPipelineParameterValues["enableFeature"]);
    }

    [Fact]
    public async Task DeployCommandFailsForUnknownPipelineParameterArgument()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        TestAppHostBackchannel? capturedBackchannel = null;

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) => (0, true, VersionHelper.GetDefaultTemplateVersion()),
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var completed = new TaskCompletionSource();
                        capturedBackchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetCapabilitiesAsyncCallback = cancellationToken => Task.FromResult(new[] { "baseline.v2", "pipeline-steps.v1", "pipeline-steps.v2", "pipeline-inputs.v1" }),
                            GetPipelineInputsAsyncCallback = (step, cancellationToken) => Task.FromResult(new GetPipelineInputsResponse
                            {
                                Inputs = [new PipelineInput { Name = "databasePassword", InputType = "SecretText" }]
                            })
                        };
                        backchannelCompletionSource?.SetResult(capturedBackchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("deploy --unknown value");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
        Assert.Null(capturedBackchannel?.AppliedPipelineParameterValues);
    }

    [Fact]
    public async Task DeployCommandFailsForMissingRequiredPipelineParameterInNonInteractiveMode()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
        {
            options.ProjectLocatorFactory = (sp) => new TestProjectLocator();

            options.DotNetCliRunnerFactory = (sp) =>
            {
                return new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (projectFile, noRestore, options, cancellationToken) => 0,
                    GetAppHostInformationAsyncCallback = (projectFile, options, cancellationToken) => (0, true, VersionHelper.GetDefaultTemplateVersion()),
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetCapabilitiesAsyncCallback = cancellationToken => Task.FromResult(new[] { "baseline.v2", "pipeline-steps.v1", "pipeline-steps.v2", "pipeline-inputs.v1" }),
                            GetPipelineInputsAsyncCallback = (step, cancellationToken) => Task.FromResult(new GetPipelineInputsResponse
                            {
                                Inputs = [new PipelineInput { Name = "databasePassword", InputType = "SecretText", Required = true }]
                            })
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

        var result = command.Parse("deploy --non-interactive");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.InvalidCommand, exitCode);
    }

    [Fact]
    public async Task DeployCommandListsPipelineInputsAsJson()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        TestAppHostBackchannel? capturedBackchannel = null;

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
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
                        var completed = new TaskCompletionSource();
                        capturedBackchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetCapabilitiesAsyncCallback = cancellationToken => Task.FromResult(new[] { "baseline.v2", "pipeline-steps.v1", "pipeline-steps.v2", "pipeline-inputs.v1" }),
                            GetPipelineInputsAsyncCallback = (step, cancellationToken) =>
                            {
                                Assert.Equal("deploy", step);
                                return Task.FromResult(new GetPipelineInputsResponse
                                {
                                    Inputs =
                                    [
                                        new PipelineInput
                                        {
                                            Name = "databasePassword",
                                            ConfigurationKey = "Parameters:databasePassword",
                                            InputType = "SecretText",
                                            Required = true,
                                            Description = "Database password.",
                                            HasValue = false
                                        },
                                        new PipelineInput
                                        {
                                            Name = "region",
                                            ConfigurationKey = "Parameters:region",
                                            InputType = "Choice",
                                            Required = false,
                                            Value = "westus2",
                                            HasValue = true,
                                            ValueSource = "configuration",
                                            Options = new Dictionary<string, string?> { ["westus2"] = "West US 2", ["eastus"] = "East US" }
                                        }
                                    ]
                                });
                            }
                        };
                        backchannelCompletionSource?.SetResult(capturedBackchannel);
                        await completed.Task.DefaultTimeout();
                        return 0;
                    }
                };
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("deploy --list-inputs --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Null(capturedBackchannel?.AppliedPipelineParameterValues);

        var output = Assert.Single(interactionService.DisplayedRawText).Text;
        Assert.Contains("\"operation\": \"deploy\"", output);
        Assert.Contains("\"step\": \"deploy\"", output);
        Assert.Contains("\"name\": \"databasePassword\"", output);
        Assert.Contains("\"configurationKey\": \"Parameters:databasePassword\"", output);
        Assert.Contains("\"environment\":", output);
        Assert.Contains("\"preferred\": \"PARAMETERS__DATABASEPASSWORD\"", output);
        Assert.Contains("\"Parameters__databasePassword\"", output);
        Assert.Contains("\"flag\": \"--database-password <value>\"", output);
        Assert.Contains("\"aliases\":", output);
        Assert.Contains("\"--Parameters:databasePassword\"", output);
        Assert.Contains("\"allowedValues\":", output);
        Assert.Contains("\"westus2\"", output);
        Assert.Contains("\"source\": \"configuration\"", output);
    }

    [Fact]
    public async Task DeployCommandListsPipelineResourcesAsJson()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var publishingActivitiesRequested = false;

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
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
                        var completed = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = completed,
                            GetCapabilitiesAsyncCallback = cancellationToken => Task.FromResult(new[] { "baseline.v2", "pipeline-steps.v1", "pipeline-steps.v2", "pipeline-resources.v1" }),
                            GetPipelineResourcesAsyncCallback = (includeHidden, cancellationToken) =>
                            {
                                Assert.False(includeHidden);
                                return Task.FromResult(new GetPipelineResourcesResponse
                                {
                                    Resources =
                                    [
                                        new ResourceSnapshot
                                        {
                                            Name = "api",
                                            DisplayName = "api",
                                            ResourceType = "Project",
                                            Relationships = [new ResourceSnapshotRelationship { ResourceName = "cache", Type = "Reference" }],
                                            Properties = new Dictionary<string, System.Text.Json.Nodes.JsonNode?> { ["projectPath"] = System.Text.Json.Nodes.JsonValue.Create("Api/Api.csproj") }
                                        },
                                        new ResourceSnapshot
                                        {
                                            Name = "cache",
                                            DisplayName = "cache",
                                            ResourceType = "Container"
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

        var result = command.Parse("deploy --list-resources --format json");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.False(publishingActivitiesRequested);

        var output = Assert.Single(interactionService.DisplayedRawText).Text;
        Assert.Contains("\"resources\":", output);
        Assert.Contains("\"name\": \"api\"", output);
        Assert.Contains("\"resourceType\": \"Project\"", output);
        Assert.Contains("\"relationships\":", output);
        Assert.Contains("\"resourceName\": \"cache\"", output);
        Assert.Contains("\"properties\":", output);
        Assert.Contains("\"projectPath\": \"Api/Api.csproj\"", output);
    }

    [Fact]
    public async Task DeployCommandSucceedsEndToEnd()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var expectedAspireHome = Path.Combine(workspace.WorkspaceRoot.FullName, ".home", ".aspire");

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
                        return (0, true, VersionHelper.GetDefaultTemplateVersion()); // Compatible app host with backchannel support
                    },

                    // Simulate apphost running successfully and establishing a backchannel
                    RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, cancellationToken) =>
                    {
                        Assert.True(options.NoLaunchProfile);
                        Assert.NotNull(env);
                        Assert.Equal(expectedAspireHome, env[KnownConfigNames.AspireHome]);

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

        using var provider = services.BuildServiceProvider();
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
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        // Use a cross-platform path for testing
        var testOutputPath = Path.Combine(Path.GetTempPath(), "test");

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

        using var provider = services.BuildServiceProvider();
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

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("deploy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(CliExitCodes.FailedToBuildArtifacts, exitCode); // Ensure the command returns a non-zero exit code

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
