// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using StreamJsonRpc;

namespace Aspire.Cli.Tests.Commands;

public class TestCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(false, 0, 23)]
    [InlineData(true, 130, 0)]
    [InlineData(true, 143, 0)]
    [InlineData(true, 137, 23)]
    [InlineData(true, 42, 23)]
    public async Task CompletionUsesTestOutcomeAndOrderlyShutdown(bool passed, int appHostExitCode, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = false;
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = ct =>
            {
                waiting.TrySetResult();
                return completion.Task.WaitAsync(ct);
            },
            RequestStopAsyncCallback = () =>
            {
                stopRequested = true;
                stopped.TrySetResult(appHostExitCode);
                return Task.CompletedTask;
            }
        };
        AppHostProjectContext? launched = null;
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                launched = context;
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await stopped.Task.WaitAsync(ct);
            }
        };
        var services = CreateServices(workspace, appHostFile, factory);
        using var provider = services.BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var execution = command.Parse(["test", "--apphost", appHostFile.FullName, "--no-build"])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await waiting.Task.DefaultTimeout();
        Assert.False(stopRequested);
        Assert.False(execution.IsCompleted);
        completion.SetResult(passed);

        Assert.Equal(expectedExitCode, await execution.DefaultTimeout());
        Assert.True(stopRequested);
        Assert.NotNull(launched);
        Assert.True(launched.IsTestRun);
        Assert.True(launched.NoBuild);
    }

    [Theory]
    [InlineData("old-host", 9)]
    [InlineData("no-tests", 2)]
    [InlineData("connection-lost", 2)]
    public async Task FailureBeforeCompletionTearsDownTheAppHost(string failure, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            GetCapabilitiesAsyncCallback = _ => Task.FromResult(failure == "old-host" ? new[] { "baseline.v2" } : new[] { "test-lifetime.v1" }),
            WaitForTestCompletionAsyncCallback = _ => failure == "no-tests"
                ? Task.FromException<bool>(new InvalidOperationException("No test resources were found."))
                : Task.FromException<bool>(new ConnectionLostException("AppHost disconnected."))
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        using var provider = CreateServices(workspace, appHostFile, factory).BuildServiceProvider();

        var exitCode = await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(expectedExitCode, exitCode);
        await tornDown.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData("test", 130)]
    [InlineData("run", 0)]
    public async Task CancellationPreservesNormalRunButDoesNotPassAnIncompleteTest(string commandName, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        using var cts = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = async ct =>
            {
                ready.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            },
            GetDashboardUrlsAsyncCallback = _ =>
            {
                if (commandName == "run")
                {
                    ready.TrySetResult();
                }
                return Task.FromResult(new DashboardUrlsState { DashboardHealthy = false });
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                Assert.Equal(commandName == "test", context.IsTestRun);
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        using var provider = CreateServices(workspace, appHostFile, factory).BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse([commandName, "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: cts.Token);

        await ready.Task.DefaultTimeout();
        cts.Cancel();

        Assert.Equal(expectedExitCode, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task AppHostExitingBeforeTestsCompleteFailsEvenWithZeroExitCode(int appHostExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = async ct =>
            {
                waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return true;
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                return await exit.Task.WaitAsync(ct);
            }
        };
        var interaction = new TestInteractionService();
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<Aspire.Cli.Interaction.IInteractionService>(interaction);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await waiting.Task.DefaultTimeout();
        exit.SetResult(appHostExitCode);

        Assert.Equal(CliExitCodes.TestRunFailed, await execution.DefaultTimeout());
        Assert.Contains(RunCommandStrings.TestAppHostExitedEarly, interaction.DisplayedErrors);
    }

    [Theory]
    [InlineData("--detach")]
    [InlineData("--format json")]
    [InlineData("--capture-profile")]
    public async Task IncompatibleOptionsFailBeforeLaunching(string options)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (_, _) => throw new InvalidOperationException("Must not launch.")
        };
        using var provider = CreateServices(workspace, CreateAppHost(workspace), factory).BuildServiceProvider();

        Assert.Equal(CliExitCodes.InvalidCommand,
            await provider.GetRequiredService<RootCommand>().Parse($"test {options}").InvokeAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(42)]
    public async Task AppHostStartupFailureNeverPasses(int exitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = (context, _) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                return Task.FromResult(exitCode);
            }
        };
        using var provider = CreateServices(workspace, appHostFile, factory).BuildServiceProvider();

        Assert.NotEqual(CliExitCodes.Success, await provider.GetRequiredService<RootCommand>()
            .Parse(["test", "--apphost", appHostFile.FullName]).InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public async Task HelpIsRegistered()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        Assert.Equal(0, await provider.GetRequiredService<RootCommand>().Parse("test --help").InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public async Task ShutdownTimeoutFailsAndCancelsTheAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHostFile = CreateAppHost(workspace);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new SignalingFakeTimeProvider(RunCommand.s_gracefulShutdownBudget);
        var backchannel = new TestAppHostBackchannel
        {
            WaitForTestCompletionAsyncCallback = _ => Task.FromResult(true),
            RequestStopAsyncCallback = () =>
            {
                stopped.TrySetResult();
                return Task.CompletedTask;
            }
        };
        var factory = new TestAppHostProjectFactory
        {
            RunAsyncCallback = async (context, ct) =>
            {
                context.BuildCompletionSource!.TrySetResult(true);
                context.BackchannelCompletionSource!.TrySetResult(backchannel);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return 0;
                }
                finally
                {
                    tornDown.TrySetResult();
                }
            }
        };
        var services = CreateServices(workspace, appHostFile, factory);
        services.AddSingleton<TimeProvider>(timeProvider);
        using var provider = services.BuildServiceProvider();
        var execution = provider.GetRequiredService<RootCommand>().Parse(["test", "--apphost", appHostFile.FullName])
            .InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);

        await stopped.Task.DefaultTimeout();
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(RunCommand.s_gracefulShutdownBudget);

        Assert.NotEqual(CliExitCodes.Success, await execution.DefaultTimeout());
        await tornDown.Task.DefaultTimeout();
    }

    private IServiceCollection CreateServices(TemporaryWorkspace workspace, FileInfo appHostFile, TestAppHostProjectFactory factory) =>
        CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.EnabledFeatures = [KnownFeatures.DefaultWatchEnabled];
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                UseOrFindAppHostProjectFileWithBehaviorAsyncCallback = (_, _, _, _) =>
                    Task.FromResult(new AppHostProjectSearchResult(appHostFile, [appHostFile]))
            };
            options.AppHostProjectFactory = _ => factory;
        });

    private static FileInfo CreateAppHost(TemporaryWorkspace workspace)
    {
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.csproj"));
        File.WriteAllText(file.FullName, "<Project />");

        return file;
    }
}
