// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
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
    /// 3. Selects Playwright CLI during skill selection
    /// 4. Verifies no errors appear (especially no "Provenance verification failed")
    /// 5. Verifies <c>playwright-cli</c> is installed and skill files are generated
    /// </summary>
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task AspireNew_WithAgentInit_InstallsPlaywrightWithoutErrors()
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

        // Create .claude folder so agent init detects a Claude Code environment.
        // This needs to exist in the workspace root before aspire new creates the project
        // because agent init chains after project creation and looks for environment markers.
        await auto.TypeAsync("mkdir -p .claude");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Run aspire new with the Starter template, going through all prompts manually
        // so we can ACCEPT the agent init prompt instead of declining it.
        await auto.TypeAsync("aspire new");
        await auto.EnterAsync();

        // Template selection: accept default Starter App
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("> Starter App").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(60),
            description: "template selection list (> Starter App)");
        await auto.EnterAsync();

        // Project name
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the project name").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "project name prompt");
        await auto.TypeAsync("StarterApp");
        await auto.EnterAsync();

        // Output path: accept default
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Enter the output path").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "output path prompt");
        await auto.EnterAsync();

        // URLs prompt: accept default No
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Use *.dev.localhost URLs").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "URLs prompt");
        await auto.EnterAsync();

        // Redis cache: accept default Yes
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Use Redis Cache").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "Redis cache prompt");
        await auto.EnterAsync();

        // Test project: accept default No
        await auto.WaitUntilAsync(
            s => new CellPatternSearcher().Find("Do you want to create a test project?").Search(s).Count > 0,
            timeout: TimeSpan.FromSeconds(10),
            description: "test project prompt");
        await auto.EnterAsync();

        // Agent init prompt: ACCEPT it (type 'y')
        await auto.WaitUntilAsync(
            s => s.ContainsText("configure AI agent environments"),
            timeout: TimeSpan.FromSeconds(120),
            description: "agent init prompt after aspire new");
        await auto.WaitAsync(500);
        await auto.TypeAsync("y");
        await auto.EnterAsync();

        // Agent init: workspace path - accept default
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);
        await auto.EnterAsync();

        // Agent init: skill location - select Claude Code
        await auto.WaitUntilAsync(
            s => s.ContainsText("skill files be installed"),
            timeout: TimeSpan.FromSeconds(60),
            description: "skill location prompt");
        await auto.TypeAsync(" "); // Toggle off default Standard location
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Claude Code location
        await auto.EnterAsync();

        // Agent init: skill selection - toggle on Playwright CLI
        await auto.WaitUntilAsync(
            s => s.ContainsText("skills should be installed"),
            timeout: TimeSpan.FromSeconds(30),
            description: "skill selection prompt");
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Playwright CLI
        await auto.EnterAsync();

        // Wait for agent init to complete (downloads @playwright/cli from npm).
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

        // Verify skill file was generated in the Claude Code location.
        await auto.TypeAsync("ls StarterApp/.claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
