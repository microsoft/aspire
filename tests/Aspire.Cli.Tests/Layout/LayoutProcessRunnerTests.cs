// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
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
}
