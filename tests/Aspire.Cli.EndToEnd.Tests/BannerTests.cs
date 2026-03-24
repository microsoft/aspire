// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire CLI banner display functionality.
/// These tests verify that the banner appears on first run and when explicitly requested.
/// </summary>
public sealed class BannerTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Banner_DisplayedOnFirstRun()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Delete the first-time use sentinel file to simulate first run
        // The sentinel is stored at ~/.aspire/cli/cli.firstUseSentinel
        // Using 'aspire cache clear' because it's not an informational
        // command and so will show the banner.
        await auto.TypeAsync("rm -f ~/.aspire/cli/cli.firstUseSentinel");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("test ! -f ~/.aspire/cli/cli.firstUseSentinel");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire cache clear");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Welcome to the") && s.ContainsText("Telemetry"),
            timeout: TimeSpan.FromSeconds(30), description: "waiting for banner and telemetry notice on first run");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task Banner_DisplayedWithExplicitFlag()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Clear screen to have a clean slate for pattern matching
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --banner");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Welcome to the") && s.ContainsText("CLI"),
            timeout: TimeSpan.FromSeconds(30), description: "waiting for banner with version info");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/14307")]
    public async Task Banner_NotDisplayedWithNoLogoFlag()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, installMode, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Delete the first-time use sentinel file to simulate first run,
        // but use --nologo to suppress the banner
        await auto.TypeAsync("rm -f ~/.aspire/cli/cli.firstUseSentinel");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --nologo --help");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            // Wait for help output to confirm command completed
            if (!s.ContainsText("Commands:"))
            {
                return false;
            }

            // Verify the banner does NOT appear
            if (s.ContainsText("Welcome to the"))
            {
                throw new InvalidOperationException(
                    "Unexpected banner displayed when --nologo flag was used!");
            }

            return true;
        }, timeout: TimeSpan.FromSeconds(30), description: "waiting for help output without banner");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
