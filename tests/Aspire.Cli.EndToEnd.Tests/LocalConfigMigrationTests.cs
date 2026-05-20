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
/// (e.g., "../apphost.mts"), the migration to aspire.config.json must re-base the path
/// to be relative to the config file's own directory (e.g., "apphost.mts").
/// </remarks>
public sealed class LocalConfigMigrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies that migrating a legacy .aspire/settings.json with a "../apphost.mts" path
    /// produces an aspire.config.json with the correct "apphost.mts" relative path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This scenario reproduces the bug where FromLegacy() copied appHostPath verbatim,
    /// without adjusting from .aspire/-relative to project-root-relative.
    /// </para>
    /// <para>
    /// The test keeps the TS project intact (apphost.mts at root with .modules/) so that
    /// aspire run can actually start successfully. The re-basing logic "../apphost.mts" →
    /// "apphost.mts" exercises the same code path as "../src/apphost.mts" → "src/apphost.mts".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task LegacySettingsMigration_AdjustsRelativeAppHostPath()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 1: Create a valid TypeScript AppHost using aspire init.
        // This produces apphost.mts, .modules/, aspire.config.json, etc.
        await auto.TypeAsync("aspire init");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Which language would you like to use?", timeout: TimeSpan.FromSeconds(30));
        await auto.DownAsync();
        await auto.WaitUntilTextAsync("> TypeScript (Node.js)", timeout: TimeSpan.FromSeconds(5));
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Created apphost.mts", timeout: TimeSpan.FromMinutes(2));
        await auto.DeclineAgentInitPromptAsync(counter);

        // Step 2: Replace aspire.config.json with a legacy .aspire/settings.json.
        // The legacy format stores appHostPath relative to the .aspire/ directory,
        // so "../apphost.mts" points up from .aspire/ to the workspace root where
        // apphost.mts lives. The project files stay in place so aspire run can work.
        await auto.TypeAsync("rm -f aspire.config.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        var legacySettingsJson = """{"appHostPath":"../apphost.mts","language":"typescript/nodejs","sdkVersion":"13.2.0","channel":"staging"}""";
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
        // The path should be "apphost.mts" (relative to workspace root),
        // NOT "../apphost.mts" (the legacy .aspire/-relative path).
        Assert.True(File.Exists(configPath), "aspire.config.json was not created by migration");
        var content = File.ReadAllText(configPath);
        Assert.DoesNotContain("\"../apphost.mts\"", content);
        Assert.Contains("\"apphost.mts\"", content);

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    [CaptureWorkspaceOnFailure]
    [Fact]
    public async Task AspireStartUpdatesStaleTypeScriptAppHostPath()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            variant: CliE2ETestHelpers.DockerfileVariant.Polyglot,
            mountDockerSocket: true,
            workspace: workspace);

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewTypeScriptEmptyAppHostAsync("StaleConfigApp", counter);

        await auto.TypeAsync("cd StaleConfigApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("sed -i 's/apphost.mts/apphost.ts/g' aspire.config.json");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.TypeAsync("aspire start");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromMinutes(3));

        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "StaleConfigApp", "aspire.config.json");
        var deadline = DateTime.UtcNow.AddMinutes(1);
        string content;
        do
        {
            content = File.ReadAllText(configPath);
            if (content.Contains("\"apphost.mts\"", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
        while (DateTime.UtcNow < deadline);

        Assert.Contains("\"apphost.mts\"", content);
        Assert.DoesNotContain("\"apphost.ts\"", content);

        await auto.TypeAsync("aspire stop --apphost apphost.mts");
        await auto.EnterAsync();
        await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromMinutes(1));

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
