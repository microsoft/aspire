// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Layout;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.LayoutTests;

public class LayoutProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_InjectsOrphanDetectionEnvironment()
    {
        IDictionary<string, string>? capturedEnv = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, _) => capturedEnv = env,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await runner.RunAsync("tool", ["arg"], ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedEnv);
        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), capturedEnv["ASPIRE_CLI_PID"]);
        Assert.True(capturedEnv.ContainsKey("ASPIRE_CLI_STARTED"));
    }

    [Fact]
    public async Task RunAsync_DoesNotOverrideCallerSuppliedOrphanEnvironment()
    {
        IDictionary<string, string>? capturedEnv = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, _) => capturedEnv = env,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        var callerEnv = new Dictionary<string, string> { ["ASPIRE_CLI_PID"] = "999" };

        await runner.RunAsync("tool", ["arg"], environmentVariables: callerEnv, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedEnv);
        Assert.Equal("999", capturedEnv["ASPIRE_CLI_PID"]);
        // The caller's dictionary must not be mutated.
        Assert.False(callerEnv.ContainsKey("ASPIRE_CLI_STARTED"));
    }

    [Fact]
    public async Task StartAsync_InjectsOrphanDetectionEnvironment()
    {
        // StartAsync launches long-lived children (aspire-managed dashboard for `aspire dashboard run`
        // and the profiling collector), so it must stamp the same orphan-detection identity as RunAsync
        // or those children cannot self-terminate when the CLI is hard-killed.
        IDictionary<string, string>? capturedEnv = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, _) => capturedEnv = env,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await using var execution = await runner.StartAsync("tool", ["arg"]);

        Assert.NotNull(capturedEnv);
        Assert.Equal(Environment.ProcessId.ToString(CultureInfo.InvariantCulture), capturedEnv["ASPIRE_CLI_PID"]);
        Assert.True(capturedEnv.ContainsKey("ASPIRE_CLI_STARTED"));
    }

    [Fact]
    public async Task StartAsync_DoesNotOverrideCallerSuppliedOrphanEnvironment()
    {
        IDictionary<string, string>? capturedEnv = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, _) => capturedEnv = env,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        var callerEnv = new Dictionary<string, string> { ["ASPIRE_CLI_PID"] = "999" };

        await using var execution = await runner.StartAsync("tool", ["arg"], environmentVariables: callerEnv);

        Assert.NotNull(capturedEnv);
        Assert.Equal("999", capturedEnv["ASPIRE_CLI_PID"]);
        // The caller's dictionary must not be mutated.
        Assert.False(callerEnv.ContainsKey("ASPIRE_CLI_STARTED"));
    }

    [Fact]
    public async Task RunAsync_WhenKillOnParentExitRequested_SetsInvocationOption()
    {
        // killOnParentExit binds the child to the Windows kill-on-close job; it must flow through to the
        // ProcessInvocationOptions the factory receives so the spawn path can assign the child to the job.
        // (On Windows this also disarms the cooperative watchdog identity — asserted separately below.)
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await runner.RunAsync("tool", ["arg"], killOnParentExit: true, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
    }

    [Fact]
    public async Task RunAsync_DefaultsKillOnParentExitToFalse()
    {
        // The backstop is opt-in: build/restore/etc. callers that don't ask for it must not be bound to
        // the kill-on-close job, preserving the existing force-kill/back-compat behavior.
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await runner.RunAsync("tool", ["arg"], ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.False(capturedOptions.KillOnParentExit);
    }

    [Fact]
    public async Task StartAsync_WhenKillOnParentExitRequested_SetsInvocationOption()
    {
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await using var execution = await runner.StartAsync("tool", ["arg"], killOnParentExit: true);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
    }

    [Fact]
    public async Task StartAsync_PreservesCallerSuppliedKillOnParentExit()
    {
        // StartAsync only ever turns the flag on, never off, so a caller that already opted in via its
        // own options is not silently downgraded when it omits the killOnParentExit argument.
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        var callerOptions = new ProcessInvocationOptions { KillOnParentExit = true };

        await using var execution = await runner.StartAsync("tool", ["arg"], options: callerOptions);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
    }

    [Fact]
    public async Task StartAsync_WhenKillOnParentExitRequested_DoesNotMutateCallerOptions()
    {
        // The killOnParentExit flag is applied to a clone, never the caller's instance, so a caller that
        // reuses a single ProcessInvocationOptions across calls is not silently bound to the kill-on-close
        // job on a later invocation that happens to request it.
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, _, _, options) => capturedOptions = options,
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        var callerOptions = new ProcessInvocationOptions { KillOnParentExit = false };

        await using var execution = await runner.StartAsync("tool", ["arg"], options: callerOptions, killOnParentExit: true);

        Assert.NotNull(capturedOptions);
        // The child was launched with the flag set...
        Assert.True(capturedOptions.KillOnParentExit);
        // ...but the caller's own instance was left untouched.
        Assert.NotSame(callerOptions, capturedOptions);
        Assert.False(callerOptions.KillOnParentExit);
    }

    [Fact]
    public async Task RunAsync_WhenKillOnParentExit_DisarmsCooperativeWatchdogOnWindowsOnly()
    {
        // The Windows kill-on-close job and the cross-platform cooperative watchdog are redundant
        // implementations of the same "don't outlive the CLI" policy; arming both on one child races the
        // job's kernel TerminateProcess against the watchdog's Environment.Exit at CLI exit and can get the child stuck. 
        // So on Windows (where the job applies) the watchdog identity is NOT stamped via environment variables, 
        // while on other hosts (where KillOnParentExit is a no-op) the watchdog is the mechanism 
        // and receivers relevant environment variables.
        IDictionary<string, string>? capturedEnv = null;
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, options) => { capturedEnv = env; capturedOptions = options; },
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await runner.RunAsync("tool", ["arg"], killOnParentExit: true, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
        Assert.NotNull(capturedEnv);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(capturedEnv.ContainsKey("ASPIRE_CLI_PID"));
        }
        else
        {
            Assert.True(capturedEnv.ContainsKey("ASPIRE_CLI_PID"));
        }
    }

    [Fact]
    public async Task StartAsync_WhenKillOnParentExit_DisarmsCooperativeWatchdogOnWindowsOnly()
    {
        // Same mutual-exclusion contract as RunAsync above, for the long-lived StartAsync helpers
        // (aspire-managed dashboard, profiling collector).
        IDictionary<string, string>? capturedEnv = null;
        ProcessInvocationOptions? capturedOptions = null;
        var factory = new TestProcessExecutionFactory
        {
            AssertionCallback = (_, env, _, options) => { capturedEnv = env; capturedOptions = options; },
            DefaultExitCode = 0,
        };
        var runner = new LayoutProcessRunner(factory);

        await using var execution = await runner.StartAsync("tool", ["arg"], killOnParentExit: true);

        Assert.NotNull(capturedOptions);
        Assert.True(capturedOptions.KillOnParentExit);
        Assert.NotNull(capturedEnv);
        if (OperatingSystem.IsWindows())
        {
            Assert.False(capturedEnv.ContainsKey("ASPIRE_CLI_PID"));
        }
        else
        {
            Assert.True(capturedEnv.ContainsKey("ASPIRE_CLI_PID"));
        }
    }
}
