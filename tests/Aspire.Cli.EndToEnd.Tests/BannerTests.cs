// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
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
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

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
            s => s.ContainsText(RootCommandStrings.BannerWelcomeText) && s.ContainsText("Telemetry"),
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
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Clear screen to have a clean slate for pattern matching
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --banner");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(RootCommandStrings.BannerWelcomeText) && s.ContainsText("CLI"),
            timeout: TimeSpan.FromSeconds(30), description: "waiting for banner with version info");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [Fact]
    public async Task Banner_NotDisplayedWithNoLogoFlag()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Delete the first-time use sentinel file to simulate first run,
        // but use --nologo to suppress the banner
        await auto.TypeAsync("rm -f ~/.aspire/cli/cli.firstUseSentinel");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire --nologo --help");
        await auto.EnterAsync();
        // Wait for the help hint that appears at the very end of help output.
        // This ensures the full help text has been rendered to the visible console
        // before we check for the absence of the banner.
        await auto.WaitUntilAsync(s =>
        {
            // Verify the banner does NOT appear
            if (s.ContainsText(RootCommandStrings.BannerWelcomeText))
            {
                throw new InvalidOperationException(
                    "Unexpected banner displayed when --nologo flag was used!");
            }

            // Only return true once the help hint is visible at the end of the output
            return s.ContainsText(HelpGroupStrings.HelpHint);
        }, timeout: TimeSpan.FromSeconds(30), description: "waiting for help output to complete");
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
