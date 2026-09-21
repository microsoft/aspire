// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test verifying that the <c>aspire new</c> flow with AI agent initialization
/// completes without provenance verification errors when installing <c>@playwright/cli</c>.
/// </summary>
[OuterloopTest("Requires npm and network access to install @playwright/cli from the npm registry")]
public sealed class NewWithAgentInitTests(ITestOutputHelper output)
{
    /// <summary>
    /// Exercises the full <c>aspire new</c> → agent init → Playwright CLI install flow end-to-end.
    /// This is the primary regression test for provenance verification failures (e.g., tag format changes
    /// in upstream <c>@playwright/cli</c> releases).
    ///
    /// The test:
    /// 1. Runs <c>aspire new</c> to create a Starter project
    /// 2. Accepts the agent init prompt (instead of declining)
    /// 3. Requests only Playwright CLI for an explicitly selected Claude Code client
    /// 4. Verifies no errors appear (especially no "Provenance verification failed")
    /// 5. Verifies <c>playwright-cli</c> is installed and skill files are generated
    /// </summary>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireNew_WithAgentInit_InstallsPlaywrightWithoutErrors()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        Assert.SkipWhen(
            strategy.Mode == CliInstallMode.InstallScript ||
            (strategy.Mode == CliInstallMode.DotnetTool && strategy.NupkgSourcePath is null),
            "This test requires the current CLI's independent agent asset options. Use a local or PR CLI build.");
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.RunCommandAsync("export CLAUDE_CONFIG_DIR=\"$PWD/.client-config/claude\"", counter);
        await auto.AspireNewAcceptingAgentInitAsync(
            "StarterApp",
            extraArguments: "--clients claude-code --playwright y --dotnet-inspect n --aspire-skills n");

        // Wait for agent init to complete (downloads @playwright/cli from npm).
        // Explicit asset/client flags avoid native client detection or Aspire source acquisition.
        // Fail the test immediately if a provenance verification error appears.
        await auto.WaitUntilAsync(s =>
        {
            if (s.ContainsText("Provenance verification failed"))
            {
                throw new InvalidOperationException(
                    "Provenance verification failed for @playwright/cli! " +
                    "This likely means the upstream package changed its tag format.");
            }
            return s.ContainsText("configuration complete");
        }, timeout: TimeSpan.FromMinutes(5), description: "agent init configuration complete (no provenance errors)");
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify playwright-cli is installed and functional.
        await auto.TypeAsync("playwright-cli --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // The one verified generation is distributed to both selected native scopes.
        await auto.TypeAsync("ls StarterApp/.claude/skills/playwright-cli/SKILL.md .client-config/claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);
    }
}
