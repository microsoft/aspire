// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying --log-level propagation to the CLI file logger and the AppHost.
/// </summary>
public sealed class LogLevelTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/17485")]
    public async Task LogLevelTrace_ProducesTraceEntriesInCliLogFile()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        using var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace, enableDcpDiagnostics: true);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a new empty AppHost project
        await auto.AspireNewCSharpEmptyAppHostAsync("LogLevelApp", counter);

        // Navigate to the AppHost directory
        await auto.TypeAsync("cd LogLevelApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Start the AppHost with --log-level trace so both the CLI and the
        // AppHost produce trace-level output in the CLI log file.
        await auto.TypeAsync("aspire start --log-level trace");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(3));

        // Stop the AppHost so the log file is flushed and closed.
        await auto.AspireStopAsync(counter);

        // Find the most recent CLI log file (the detached child writes its own log).
        // The detached process log usually contains "detach" in the name.
        await auto.TypeAsync(
                "DETACH_LOG=$(ls -t ~/.aspire/logs/cli_*detach*.log 2>/dev/null | head -1); " +
                "if [ -z \"$DETACH_LOG\" ]; then DETACH_LOG=$(ls -t ~/.aspire/logs/cli_*.log 2>/dev/null | head -1); fi; " +
                "echo \"LOG_FILE:$DETACH_LOG\"");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Check for trace-level AppHost log entry (format: [TRCE] [AppHost/...])
        await auto.RunCommandAsync(
                "test -n \"$DETACH_LOG\" && grep -q '\\[TRCE\\] \\[AppHost/' \"$DETACH_LOG\"",
                counter,
                TimeSpan.FromSeconds(10));

        // Check for trace-level CLI log entry from the Features category
        await auto.RunCommandAsync(
                "test -n \"$DETACH_LOG\" && grep -q '\\[TRCE\\] \\[Features\\]' \"$DETACH_LOG\"",
                counter,
                TimeSpan.FromSeconds(10));
    }
}
