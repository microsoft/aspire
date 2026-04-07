// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DoCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DoCommandWithHelpArgumentReturnsZero()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper);
        var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("do --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DoCommandWithStepArgumentSucceeds()
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

        var provider = services.BuildServiceProvider();
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
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
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

        var provider = services.BuildServiceProvider();
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
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
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

        var provider = services.BuildServiceProvider();
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
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        // Arrange
        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper, options =>
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

        var provider = services.BuildServiceProvider();
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
                        return (1, false, null);
                    }
                };
                return runner;
            };
        });

        var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        // Act
        var result = command.Parse("do my-step --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        // Assert
        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }
}
