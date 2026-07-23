// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public void VerifyOption_IsDeclaredOnlyByPublish()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var publish = provider.GetRequiredService<PublishCommand>();
        var deploy = provider.GetRequiredService<DeployCommand>();

        Assert.Contains(publish.Options, option => option.Name == "--verify");
        Assert.DoesNotContain(deploy.Options, option => option.Name == "--verify");
    }

    [Fact]
    public async Task PublishCommand_ExtensionDebugRelaunch_ForwardsMatchedAndUnmatchedArguments()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        DebugSessionOptions? debugSessionOptions = null;
        string? projectFile = null;
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.CliHostEnvironmentFactory = serviceProvider =>
                new CliHostEnvironment(
                    serviceProvider.GetRequiredService<IConfiguration>(),
                    nonInteractive: false);
            options.InteractionServiceFactory = serviceProvider =>
            {
                var service = new TestExtensionInteractionService(serviceProvider)
                {
                    StartDebugSessionCallback = (_, appHost, _, sessionOptions) =>
                    {
                        projectFile = appHost;
                        debugSessionOptions = sessionOptions;
                    }
                };
                return service;
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "generated output");

        var result = command.Parse(
            $"publish --apphost \"{appHostFile.FullName}\" --output-path \"{outputPath}\" " +
            "--verify --pipeline-log-level debug --environment staging " +
            "--include-exception-details --no-build --publisher custom");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(appHostFile.FullName, projectFile);
        Assert.NotNull(debugSessionOptions);
        Assert.Equal("publish", debugSessionOptions.Command);
        Assert.Equal(
        [
            "--output-path",
            outputPath,
            "--pipeline-log-level",
            "debug",
            "--environment",
            "staging",
            "--include-exception-details",
            "--no-build",
            "--verify",
            "--",
            "--publisher",
            "custom"
        ],
            debugSessionOptions.Args!);
    }

    [Fact]
    public async Task PublishVerification_PreservesAppHostRuntimeExitCodeAndCleansStaging()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var targetDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "generated");
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };
        var git = new TestGitRepository
        {
            GetRootFromDirectoryAsyncCallback = (_, _) =>
                Task.FromResult<DirectoryInfo?>(workspace.WorkspaceRoot),
            GetIncludedFilesFromPathsAsyncCallback = (_, _, _) =>
                Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>()),
            GetIgnoredFilesAsyncCallback = (_, _, _) =>
                Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>())
        };
        string? stagingRoot = null;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.GitRepositoryFactory = _ => git;
            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner
                {
                    BuildAsyncCallback = (_, _, _, _) => 0,
                    GetAppHostInformationAsyncCallback = (_, _, _) =>
                        (0, true, VersionHelper.GetDefaultTemplateVersion())
                };
                runner.RunAsyncCallback = async (_, _, _, _, arguments, _, completionSource, _, cancellationToken) =>
                {
                    var outputPathIndex = Array.IndexOf(arguments, "--output-path");
                    Assert.True(outputPathIndex >= 0);
                    var stagedPrimary = arguments[outputPathIndex + 1];
                    stagingRoot = Directory.GetParent(stagedPrimary)!.FullName;
                    Assert.Contains(
                        arguments,
                        argument => argument == $"Pipeline:Verification:StagingPath={stagingRoot}");
                    Assert.Contains(
                        arguments,
                        argument => argument == $"Pipeline:Verification:TargetOutputPath={targetDirectory}");

                    var planCall = 0;
                    var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var backchannel = new TestAppHostBackchannel
                    {
                        RequestStopAsyncCalled = stopped,
                        GetCapabilitiesAsyncCallback = _ =>
                            Task.FromResult<string[]>(["baseline.v2", "pipeline-outputs.v1"]),
                        GetPipelineOutputsAsyncCallback = _ => Task.FromResult(new GetPipelineOutputsResponse
                        {
                            AppHostDirectory = appHostDirectory.FullName,
                            State = planCall++ == 0 ? "Prepared" : "Succeeded",
                            Steps =
                            [
                                new PipelineOutputStepInfo
                                {
                                    Name = "publish",
                                    SupportsOutputPathRelocation = true
                                }
                            ],
                            Outputs =
                            [
                                new PipelineOutputInfo
                                {
                                    PublisherName = "aspire",
                                    Name = "primary",
                                    Kind = "Directory",
                                    OutputPath = stagedPrimary,
                                    LogicalTargetPath = targetDirectory
                                }
                            ]
                        })
                    };
                    completionSource!.SetResult(backchannel);
                    await stopped.Task.WaitAsync(cancellationToken);
                    return 42;
                };
                return runner;
            };
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(
            $"publish --verify --apphost \"{appHostFile.FullName}\" --output-path \"{targetDirectory}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(42, exitCode);
        Assert.NotNull(stagingRoot);
        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(Directory.Exists(targetDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("13.4.6")]
    public async Task PublishVerification_OldOrUnknownHosting_FailsBeforeGitOrLaunch(string? hostingVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostDirectory = workspace.WorkspaceRoot.CreateSubdirectory("AppHost");
        var appHostFile = new FileInfo(Path.Combine(appHostDirectory.FullName, "AppHost.csproj"));
        await File.WriteAllTextAsync(appHostFile.FullName, "<Project />");
        var targetDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "generated");
        var projectLocator = new TestProjectLocator
        {
            UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
        };
        var projectFactory = new TestAppHostProjectFactory
        {
            GetAspireHostingVersionAsyncCallback = (_, _) =>
                Task.FromResult(hostingVersion)
        };
        var gitCalled = false;
        var git = new TestGitRepository
        {
            GetRootFromDirectoryAsyncCallback = (_, _) =>
            {
                gitCalled = true;
                return Task.FromResult<DirectoryInfo?>(workspace.WorkspaceRoot);
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.AppHostProjectFactory = _ => projectFactory;
            options.GitRepositoryFactory = _ => git;
        });
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse(
            $"publish --verify --apphost \"{appHostFile.FullName}\" --output-path \"{targetDirectory}\"");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.AppHostIncompatible, exitCode);
        Assert.False(gitCalled);
        Assert.False(Directory.Exists(targetDirectory));
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
