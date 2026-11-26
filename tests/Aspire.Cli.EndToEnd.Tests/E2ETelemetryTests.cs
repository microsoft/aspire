// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class E2ETelemetryTests(ITestOutputHelper output)
{
    [Fact]
    public Task ChildProcessesInheritOptOutAndCanOverrideIt() =>
        E2ETelemetryEnvironmentTests.ChildProcessesInheritOptOutAndCanOverrideIt();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ShellEnvironmentSetupOptsOutOfProductTelemetry(bool includeBundlePath) =>
        E2ETelemetryEnvironmentTests.ShellEnvironmentSetupOptsOutOfProductTelemetry(includeBundlePath);

    [Fact]
    public async Task DockerTerminalStartsWithProductTelemetryOptedOut()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        using var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(60));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.WaitUntilTextAsync("# ", timeout: TimeSpan.FromSeconds(60));
        await auto.TypeAsync(AspireCliShellCommandHelpers.NumberedPromptSetupCommand);
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.RunCommandAsync(
            """test "$ASPIRE_CLI_TELEMETRY_OPTOUT" = true && test "$ASPIRE_DASHBOARD_TELEMETRY_OPTOUT" = true""",
            counter);
    }
}
