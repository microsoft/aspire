// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for the <c>.aspire-update.json</c> self-update detection mechanism.
/// Verifies that the CLI correctly reads the sidecar config file and disables self-update
/// with appropriate messaging when the file is present.
/// </summary>
public sealed class SelfUpdateDetectionTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies that <c>aspire update --self</c> shows a "Self-update is disabled" message
    /// when <c>.aspire-update.json</c> exists next to the binary with <c>selfUpdateDisabled: true</c>.
    /// </summary>
    [Fact]
    public async Task UpdateSelf_WithAspireUpdateJson_ShowsDisabledMessage()
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

        // Create .aspire-update.json next to the CLI binary to simulate a package-manager install
        await auto.TypeAsync("""echo '{"selfUpdateDisabled":true}' > ~/.aspire/bin/.aspire-update.json""");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire update --self and expect the disabled message
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(UpdateCommandStrings.SelfUpdateDisabledMessage),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for self-update disabled message");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that <c>aspire update --self</c> shows custom update instructions
    /// when <c>.aspire-update.json</c> includes the <c>updateInstructions</c> field.
    /// This simulates a Homebrew or WinGet install where users should use the package manager instead.
    /// </summary>
    [Fact]
    public async Task UpdateSelf_WithCustomInstructions_ShowsInstructions()
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

        // Create .aspire-update.json with custom instructions (simulating Homebrew)
        var customInstructions = "brew upgrade --cask aspire";
        await auto.TypeAsync("echo '{\"selfUpdateDisabled\":true,\"updateInstructions\":\"" + customInstructions + "\"}' > ~/.aspire/bin/.aspire-update.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire update --self and expect both the disabled message and custom instructions
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --self");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText(UpdateCommandStrings.SelfUpdateDisabledMessage) && s.ContainsText(customInstructions),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for self-update disabled message with custom instructions");
        await auto.WaitForSuccessPromptAsync(counter);

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that <c>aspire update --self</c> proceeds normally (does NOT show disabled message)
    /// when there is no <c>.aspire-update.json</c> file next to the binary.
    /// This is the default case for direct installs via the install script.
    /// </summary>
    [Fact]
    public async Task UpdateSelf_WithoutAspireUpdateJson_DoesNotShowDisabledMessage()
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

        // Ensure no .aspire-update.json exists (it shouldn't after normal install, but be explicit)
        await auto.TypeAsync("rm -f ~/.aspire/bin/.aspire-update.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire update --self --channel stable (provide channel to skip the prompt)
        // The command will either proceed with download or show an error — but should NOT
        // show the "Self-update is disabled" message.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire update --self --channel stable");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(s =>
        {
            // Fail fast if we see the disabled message — it should NOT appear
            if (s.ContainsText(UpdateCommandStrings.SelfUpdateDisabledMessage))
            {
                throw new InvalidOperationException(
                    "Unexpected 'Self-update is disabled' message when no .aspire-update.json exists!");
            }

            // The command should proceed past the installation check.
            // It may fail for other reasons (network, version check, etc.) — that's OK.
            // We just need to verify the disabled message didn't appear.
            // Wait for the prompt to return (success or error).
            return s.ContainsText("OK]") || s.ContainsText("ERR:");
        }, timeout: TimeSpan.FromSeconds(60), description: "waiting for update --self to complete without disabled message");

        // Exit the shell
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
