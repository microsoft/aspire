// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Watch;
using Aspire.Shared;

namespace Aspire.Cli.Tests;

public class DotnetWatchServerTests
{
    [Fact]
    public async Task ServerRetainsTheSelectedSdkInsideAnOwnedProcessScope()
    {
        var dotnet = Path.Combine(AppContext.BaseDirectory, "selected-sdk", "dotnet");
        var workingDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var factory = new TestProcessExecutionFactory
        {
            AttemptCallback = (_, options) =>
            {
                options.StandardOutputCallback?.Invoke("server stdout");
                options.StandardErrorCallback?.Invoke("compiler diagnostic");
                return (17, null);
            }
        };
        string[] arguments = [dotnet, "exec", "watch.dll", "server", "--sdk", "selected-sdk", "--resource", "Service.csproj"];

        var result = await DotnetWatchServer.RunAsync(
            arguments, factory, workingDirectory, standardOutput, standardError, TestContext.Current.CancellationToken);

        Assert.Equal(17, result);
        Assert.Equal(dotnet, factory.LastFileName);
        Assert.Equal(arguments[1..], factory.LastArguments);
        Assert.Same(workingDirectory, factory.LastWorkingDirectory);
        Assert.Null(factory.LastEnvironmentVariables);
        Assert.Equal(ChildProcessLifetime.OwnedTree, factory.LastProcessInvocationOptions!.Lifetime);
        Assert.True(factory.LastProcessInvocationOptions.SuppressLogging);
        Assert.True(factory.LastProcessInvocationOptions.EnvironmentVariableFilter!(DotnetWatchConfiguration.ScopeCompletionPath));
        Assert.False(factory.LastProcessInvocationOptions.EnvironmentVariableFilter("APP_SETTING"));
        Assert.Equal("server stdout" + Environment.NewLine, standardOutput.ToString());
        Assert.Equal("compiler diagnostic" + Environment.NewLine, standardError.ToString());
        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
    }

    [Fact]
    public async Task UnexpectedInvocationDoesNotStartAChild()
    {
        var factory = new TestProcessExecutionFactory();
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchServer.RunAsync(
            ["dotnet", "run"], factory, new DirectoryInfo(AppContext.BaseDirectory),
            standardOutput, standardError, TestContext.Current.CancellationToken));

        Assert.Empty(factory.CreatedExecutions);
    }

    [Fact]
    public async Task CancellationDisposesTheOwnedExecution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new TestProcessExecutionFactory
        {
            AsyncAttemptCallback = async (_, _, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return (0, null);
            }
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();
        var run = DotnetWatchServer.RunAsync(
            [Path.Combine(AppContext.BaseDirectory, "dotnet"), "exec", "watch.dll", "server"],
            factory, new DirectoryInfo(AppContext.BaseDirectory), standardOutput, standardError, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
    }

    [Fact]
    public async Task StartFailureDisposesTheExecutionAndReportsFailure()
    {
        var factory = new TestProcessExecutionFactory
        {
            CreateExecutionWithFileNameCallback = (name, arguments, environment, _, options) =>
                new TestProcessExecution(name, arguments, environment, options,
                    (_, _, _) => Task.FromResult((0, (string?)null)), () => 1)
                {
                    StartReturnValue = false
                }
        };
        using var standardOutput = new StringWriter();
        using var standardError = new StringWriter();

        await Assert.ThrowsAsync<InvalidOperationException>(() => DotnetWatchServer.RunAsync(
            [Path.Combine(AppContext.BaseDirectory, "dotnet"), "exec", "watch.dll", "server"],
            factory, new DirectoryInfo(AppContext.BaseDirectory), standardOutput, standardError,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, Assert.IsType<TestProcessExecution>(Assert.Single(factory.CreatedExecutions)).DisposeCount);
    }
}
