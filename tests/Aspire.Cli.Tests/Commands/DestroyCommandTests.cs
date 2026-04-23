// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class DestroyCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DestroyCommandWithHelpArgumentReturnsZero()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("destroy --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DestroyCommandFailsWithInvalidProjectFile()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

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

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("destroy --apphost invalid.csproj");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.FailedToFindProject, exitCode);
    }

    [Fact]
    public async Task DestroyCommandPassesCorrectStepArgument()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

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
                        Assert.True(options.NoLaunchProfile);

                        Assert.Contains("--operation", args);
                        Assert.Contains("publish", args);
                        Assert.Contains("--step", args);
                        Assert.Contains("destroy", args);

                        var destroyCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = destroyCompleted
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await destroyCompleted.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestDeployCommandPrompter(interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("destroy");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DestroyCommandForwardsYesFlag()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);

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
                        Assert.Contains("--yes", args);
                        Assert.Contains("true", args);
                        Assert.Contains("--step", args);
                        Assert.Contains("destroy", args);

                        var destroyCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = destroyCompleted
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await destroyCompleted.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestDeployCommandPrompter(interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse("destroy --yes");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task DestroyCommandIncludesOutputPathWhenSpecified()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        var testOutputPath = Path.Combine(Path.GetTempPath(), "test-destroy");

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
                        Assert.Contains("--output-path", args);
                        Assert.Contains(testOutputPath, args);
                        Assert.Contains("--step", args);
                        Assert.Contains("destroy", args);

                        var destroyCompleted = new TaskCompletionSource();
                        var backchannel = new TestAppHostBackchannel
                        {
                            RequestStopAsyncCalled = destroyCompleted
                        };
                        backchannelCompletionSource?.SetResult(backchannel);
                        await destroyCompleted.Task.DefaultTimeout();
                        return 0;
                    }
                };

                return runner;
            };

            options.PublishCommandPrompterFactory = (sp) =>
            {
                var interactionService = sp.GetRequiredService<IInteractionService>();
                return new TestDeployCommandPrompter(interactionService);
            };
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();

        var result = command.Parse($"destroy --output-path {testOutputPath}");
        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(0, exitCode);
    }
}
