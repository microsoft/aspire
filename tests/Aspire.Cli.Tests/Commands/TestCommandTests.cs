// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class TestCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task TestCommand_WhenAllTestResourcesPass_ReturnsSuccess()
    {
        var appHostExitCode = new TaskCompletionSource<int>();
        var requestStopCalled = new TaskCompletionSource();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                // The CLI controls the lifetime: the app host does not exit until the test command asks it
                // to stop, which completes this task.
                return await appHostExitCode.Task.WaitAsync(ct);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                RequestStopAsyncCalled = requestStopCalled,
                RequestStopAsyncCallback = () =>
                {
                    appHostExitCode.TrySetResult(0);
                    return Task.CompletedTask;
                },
                GetTestRunUpdatesAsyncCallback = PassingTestRunAsync,
                GetAppHostLogEntriesAsyncCallback = EmptyLogEntriesAsync
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("test");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(requestStopCalled.Task.IsCompleted, "The test command should stop the app host once the test run completes.");
    }

    [Fact]
    public async Task TestCommand_WhenATestResourceFails_ReturnsTestRunFailed()
    {
        var appHostExitCode = new TaskCompletionSource<int>();

        var runnerFactory = (IServiceProvider sp) =>
        {
            var runner = new TestDotNetCliRunner();
            runner.BuildAsyncCallback = (projectFile, noRestore, options, ct) => 0;
            runner.GetAppHostInformationAsyncCallback = (projectFile, options, ct) => (0, true, VersionHelper.GetDefaultTemplateVersion());
            runner.RunAsyncCallback = async (projectFile, watch, noBuild, noRestore, args, env, backchannelCompletionSource, options, ct) =>
            {
                var backchannel = sp.GetRequiredService<IAppHostCliBackchannel>();
                backchannelCompletionSource!.SetResult(backchannel);

                return await appHostExitCode.Task.WaitAsync(ct);
            };

            return runner;
        };

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.AppHostBackchannelFactory = _ => new TestAppHostBackchannel
            {
                // The app host exits cleanly even though a test failed; success is determined by the
                // streamed results, not the process exit code.
                RequestStopAsyncCallback = () =>
                {
                    appHostExitCode.TrySetResult(0);
                    return Task.CompletedTask;
                },
                GetTestRunUpdatesAsyncCallback = FailingTestRunAsync,
                GetAppHostLogEntriesAsyncCallback = EmptyLogEntriesAsync
            };
            options.DotNetCliRunnerFactory = runnerFactory;
        });

        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("test");

        var exitCode = await result.InvokeAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        Assert.Equal(CliExitCodes.TestRunFailed, exitCode);
    }

    private static async IAsyncEnumerable<TestRunUpdate> PassingTestRunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new TestRunUpdate { Resource = "integration-tests", Status = KnownTestRunStatuses.Running };
        yield return new TestRunUpdate { Resource = "integration-tests", Status = KnownTestRunStatuses.Passed, Detail = "Finished" };
    }

    private static async IAsyncEnumerable<TestRunUpdate> FailingTestRunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new TestRunUpdate { Resource = "integration-tests", Status = KnownTestRunStatuses.Running };
        yield return new TestRunUpdate { Resource = "integration-tests", Status = KnownTestRunStatuses.Failed, Detail = "Exited (exit code 1)" };
    }

    private static async IAsyncEnumerable<BackchannelLogEntry> EmptyLogEntriesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield break;
    }
}
