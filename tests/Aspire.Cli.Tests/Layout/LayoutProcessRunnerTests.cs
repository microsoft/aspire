// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;

namespace Aspire.Cli.Tests.LayoutTests;

public class LayoutProcessRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelpersUseOwnedTreeWithoutCooperativeWatchdog(bool background)
    {
        IDictionary<string, string>? capturedEnvironment = null;
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, environment, _, options) =>
            {
                capturedEnvironment = environment;
                capturedOptions = options;
            }
        };
        var runner = new LayoutProcessRunner(factory);

        if (background)
        {
            await using var execution = await runner.StartAsync("tool", ["arg"]);
        }
        else
        {
            await runner.RunAsync("tool", ["arg"], ct: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(capturedOptions);
        Assert.Equal(ChildProcessLifetime.OwnedTree, capturedOptions.Lifetime);
        Assert.NotNull(capturedEnvironment);
        Assert.Empty(capturedEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelpersPreserveCallerEnvironmentWithoutMutatingIt(bool background)
    {
        IDictionary<string, string>? capturedEnvironment = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, environment, _, _) => capturedEnvironment = environment
        };
        var runner = new LayoutProcessRunner(factory);
        var environment = new Dictionary<string, string> { ["CUSTOM"] = "value" };

        if (background)
        {
            await using var execution = await runner.StartAsync("tool", ["arg"], environmentVariables: environment);
        }
        else
        {
            await runner.RunAsync("tool", ["arg"], environmentVariables: environment, ct: TestContext.Current.CancellationToken);
        }

        Assert.NotNull(capturedEnvironment);
        Assert.Equal(new KeyValuePair<string, string>("CUSTOM", "value"), Assert.Single(capturedEnvironment));
        Assert.Equal(new KeyValuePair<string, string>("CUSTOM", "value"), Assert.Single(environment));
        Assert.NotSame(environment, capturedEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAsync_ClonesOptionsAndPreservesParentExitOptIn(bool optInArgument)
    {
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options
        };
        var runner = new LayoutProcessRunner(factory);
        var callerOptions = new ProcessInvocationOptions { KillOnParentExit = !optInArgument };

        await using var execution = await runner.StartAsync("tool", ["arg"], options: callerOptions, killOnParentExit: optInArgument);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
        Assert.Equal(ChildProcessLifetime.OwnedTree, capturedOptions.Lifetime);
        Assert.NotSame(callerOptions, capturedOptions);
        Assert.Equal(!optInArgument, callerOptions.KillOnParentExit);
        Assert.Equal(ChildProcessLifetime.CallerManaged, callerOptions.Lifetime);
    }

    [Fact]
    public async Task StartAsync_DetachedChildDoesNotBecomeAnOwnedTree()
    {
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options
        };
        var runner = new LayoutProcessRunner(factory);

        await using var execution = await runner.StartAsync("tool", ["arg"], options: new ProcessInvocationOptions { Detached = true });

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.Detached);
        Assert.Equal(ChildProcessLifetime.CallerManaged, capturedOptions.Lifetime);
    }

    [Fact]
    public async Task StartAsync_AppHostKeepsItsDistinctCleanupPolicy()
    {
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options
        };
        var runner = new LayoutProcessRunner(factory);

        await using var execution = await runner.StartAsync("tool", ["arg"],
            options: new ProcessInvocationOptions { Lifetime = ChildProcessLifetime.AppHost }, killOnParentExit: true);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
        Assert.Equal(ChildProcessLifetime.AppHost, capturedOptions.Lifetime);
    }
}
