// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying that legacy .aspire/settings.json files with relative paths
/// are correctly migrated to aspire.config.json with adjusted paths.
/// </summary>
/// <remarks>
/// When .aspire/settings.json stores appHostPath relative to the .aspire/ directory
/// (e.g., "../apphost.ts"), the migration to aspire.config.json must re-base the path
/// to be relative to the config file's own directory (e.g., "apphost.ts").
/// </remarks>
public sealed class LocalConfigMigrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies that migrating a legacy .aspire/settings.json with a "../apphost.ts" path
    /// produces an aspire.config.json with the correct "apphost.ts" relative path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This scenario reproduces the bug where FromLegacy() copied appHostPath verbatim,
    /// without adjusting from .aspire/-relative to project-root-relative.
    /// </para>
    /// <para>
    /// The test keeps the TS project intact (apphost.ts at root with .modules/) so that
    /// aspire run can actually start successfully. The re-basing logic "../apphost.ts" →
    /// "apphost.ts" exercises the same code path as "../src/apphost.ts" → "src/apphost.ts".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LegacySettingsMigration_AdjustsRelativeAppHostPath()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var installMode = CliE2ETestHelpers.DetectDockerInstallMode(repoRoot);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, installMode, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliInDockerAsync(installMode, counter);

        // Step 1: Create a valid TypeScript AppHost using aspire init.
        // This produces apphost.ts, .modules/, aspire.config.json, etc.
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.ts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Step 2: Replace aspire.config.json with a legacy .aspire/settings.json.
        // The legacy format stores appHostPath relative to the .aspire/ directory,
        // so "../apphost.ts" points up from .aspire/ to the workspace root where
        // apphost.ts lives. The project files stay in place so aspire run can work.
        await auto.TypeAsync("rm -f aspire.config.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var legacySettingsJson = """{"appHostPath":"../apphost.ts","language":"typescript/nodejs","sdkVersion":"13.2.0","channel":"staging"}""";
        await auto.TypeAsync($"mkdir -p .aspire && echo '{legacySettingsJson}' > .aspire/settings.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Run aspire run to trigger the migration from .aspire/settings.json
        // to aspire.config.json. The migration happens during apphost discovery,
        // before the actual build/run step.
        await auto.TypeAsync("aspire run");
        await auto.EnterAsync();

        // The migration creates aspire.config.json during apphost discovery, before
        // the actual run. Poll the host-side filesystem via the bind mount rather
        // than parsing terminal output, which is fragile across different failure modes.
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(configPath) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        // Stop the apphost if it's still running (Ctrl+C is safe even if already exited)
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        // Step 4: Verify aspire.config.json was created with the corrected path.
        // The path should be "apphost.ts" (relative to workspace root),
        // NOT "../apphost.ts" (the legacy .aspire/-relative path).
        Assert.True(File.Exists(configPath), "aspire.config.json was not created by migration");
        var content = File.ReadAllText(configPath);
        Assert.DoesNotContain("\"../apphost.ts\"", content);
        Assert.Contains("\"apphost.ts\"", content);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
