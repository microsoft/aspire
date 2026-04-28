// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end test verifying that the Playwright CLI installation flow works correctly
/// through <c>aspire agent init</c>, including npm provenance verification and skill file generation.
/// </summary>
[OuterloopTest("Requires npm and network access to install @playwright/cli from the npm registry")]
public sealed class PlaywrightCliInstallTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies the full Playwright CLI installation lifecycle:
    /// 1. Playwright CLI is not initially installed
    /// 2. An Aspire project is created
    /// 3. <c>aspire agent init</c> is run with Claude Code environment selected
    /// 4. Playwright CLI is installed and available on PATH
    /// 5. The <c>.claude/skills/playwright-cli/SKILL.md</c> skill file is generated
    /// </summary>
    [Fact]
    public async Task AgentInit_InstallsPlaywrightCli_AndGeneratesSkillFiles()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Verify playwright-cli is not installed.
        await auto.TypeAsync("playwright-cli --version 2>&1 || true");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Create an Aspire project (accept all defaults).
        await auto.AspireNewAsync("TestProject", counter);

        // Step 3: Navigate into the project and create .claude folder to trigger Claude Code detection.
        await auto.TypeAsync("cd TestProject && mkdir -p .claude");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 4: Run aspire agent init.
        // First prompt: workspace path
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);
        await auto.EnterAsync(); // Accept default workspace path

        // Second prompt: skill locations. Select Claude Code only.
        await auto.WaitUntilAsync(
            s => s.ContainsText("skill files be installed"),
            timeout: TimeSpan.FromSeconds(60), description: "skill location prompt");
        await auto.TypeAsync(" "); // Toggle off default Standard location
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Claude Code location
        await auto.EnterAsync();

        // Third prompt: skills. Aspire is pre-selected; toggle on Playwright CLI too.
        await auto.WaitUntilAsync(
            s => s.ContainsText("skills should be installed"),
            timeout: TimeSpan.FromSeconds(30), description: "skill selection prompt");
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Playwright CLI
        await auto.EnterAsync();

        // Wait for installation to complete (this downloads from npm, can take a while)
        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Verify playwright-cli is now installed.
        await auto.TypeAsync("playwright-cli --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 6: Verify the skill file was generated.
        await auto.TypeAsync("ls .claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that when <c>aspire agent init</c> is run from a different directory than the
    /// workspace root, <c>playwright-cli install --skills</c> generates skill files in the
    /// workspace root, not the current working directory.
    ///
    /// This is a regression test for https://github.com/microsoft/aspire/issues/15140 where
    /// the missing <c>WorkingDirectory</c> on <c>ProcessStartInfo</c> caused skill files
    /// to be dropped in the CLI process's current working directory.
    /// </summary>
    [Fact]
    public async Task AgentInit_WhenCwdDiffersFromWorkspaceRoot_PlacesSkillFilesInWorkspaceRoot()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create an Aspire project.
        await auto.AspireNewAsync("TestProject", counter);

        // Step 2: Create .claude folder inside the project to trigger Claude Code detection.
        // Crucially, do NOT cd into the project — stay in the parent directory.
        await auto.TypeAsync("mkdir -p TestProject/.claude");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptFailFastAsync(counter);

        // Step 3: Run aspire agent init from the PARENT directory.
        // When prompted for workspace root, provide the project subdirectory.
        await auto.TypeAsync("aspire agent init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("workspace:", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(500);

        // Clear the default path and type the project subdirectory instead.
        // The default will be the git root or CWD — we need to override it.
        await auto.Ctrl().KeyAsync(Hex1bKey.U); // Clear the line
        await auto.TypeAsync("TestProject");
        await auto.EnterAsync();

        // Select Claude Code as the only skill location.
        await auto.WaitUntilAsync(
            s => s.ContainsText("skill files be installed"),
            timeout: TimeSpan.FromSeconds(60), description: "skill location prompt");
        await auto.TypeAsync(" "); // Toggle off default Standard location
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Claude Code location
        await auto.EnterAsync();

        // Select skills. Aspire is pre-selected; toggle on Playwright CLI too.
        await auto.WaitUntilAsync(
            s => s.ContainsText("skills should be installed"),
            timeout: TimeSpan.FromSeconds(30), description: "skill selection prompt");
        await auto.DownAsync();
        await auto.TypeAsync(" "); // Toggle on Playwright CLI
        await auto.EnterAsync();

        await auto.WaitUntilTextAsync("configuration complete", timeout: TimeSpan.FromMinutes(3));
        await auto.WaitForSuccessPromptFailFastAsync(counter);

        // Step 4: Verify skill file exists in the workspace root (project subdirectory).
        await auto.TypeAsync("ls TestProject/.claude/skills/playwright-cli/SKILL.md");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("SKILL.md", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptFailFastAsync(counter);

        // Step 5: Verify no stray skill files were created in the CWD (parent directory).
        await auto.TypeAsync("test -d .claude/skills/playwright-cli && echo 'STRAY_FILES_FOUND' || echo 'NO_STRAY_FILES'");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("NO_STRAY_FILES", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptFailFastAsync(counter);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
