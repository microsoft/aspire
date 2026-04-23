// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying that <c>aspire.config.json</c> is discovered from the
/// apphost's directory rather than being recreated in the current working directory.
/// </summary>
/// <remarks>
/// Reproduces the bug where <c>aspire new myproject</c> creates the config inside
/// <c>myproject/</c>, but running <c>aspire run</c> from the parent directory
/// creates a spurious <c>aspire.config.json</c> in the parent instead of finding
/// the one adjacent to <c>apphost.ts</c>.
/// </remarks>
public sealed class ConfigDiscoveryTests(ITestOutputHelper output)
{
    /// <summary>
    /// Verifies that running <c>aspire run</c> from a parent directory discovers the
    /// existing <c>aspire.config.json</c> next to the apphost rather than creating a
    /// new one in the current working directory.
    /// </summary>
    [Fact]
    public async Task RunFromParentDirectory_UsesExistingConfigNearAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect();
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

        const string projectName = "ConfigTest";

        // Step 1: Create a TypeScript Empty AppHost project.
        // This creates a subdirectory with aspire.config.json inside it.
        await auto.AspireNewAsync(projectName, counter, template: AspireTemplate.TypeScriptEmptyAppHost);

        // Capture the original config content before running from the parent directory.
        var projectConfigPath = Path.Combine(
            workspace.WorkspaceRoot.FullName, projectName, "aspire.config.json");
        var parentConfigPath = Path.Combine(
            workspace.WorkspaceRoot.FullName, "aspire.config.json");

        // Verify the project config was created by aspire new
        Assert.True(File.Exists(projectConfigPath),
            $"aspire new should have created {projectConfigPath}");

        var originalContent = File.ReadAllText(projectConfigPath);

        // Step 2: Stay in the parent directory (do NOT cd into the project).
        // Run aspire run — this should find the apphost in the subdirectory
        // and use the adjacent aspire.config.json, not create a new one in CWD.
        // Run aspire run — this should find the apphost in the subdirectory
        // and use the adjacent aspire.config.json, not create a new one in CWD.
        await auto.TypeAsync($"aspire run --apphost {projectName}");
        await auto.EnterAsync();

        // Wait for the run to start (or fail) — either way the config discovery has happened.
        await auto.WaitUntilAsync(s =>
        {
            // If a "Select an apphost" prompt appears, the bug may have caused multiple detection
            if (s.ContainsText("Select an AppHost to use:"))
            {
                throw new InvalidOperationException("Multiple apphosts incorrectly detected");
            }

            return s.ContainsText("Press CTRL+C to stop the AppHost and exit.")
                || s.ContainsText("ERR:");
        }, timeout: TimeSpan.FromMinutes(3), description: "aspire run started or errored");

        // Stop the apphost
        await auto.Ctrl().KeyAsync(Hex1bKey.C);
        await auto.WaitForAnyPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        // Step 3: Assertions on file system state (host-side via bind mount).

        // The parent directory should NOT have an aspire.config.json.
        Assert.False(File.Exists(parentConfigPath),
            $"aspire.config.json should NOT be created in the parent/CWD directory. " +
            $"Found: {parentConfigPath}");

        // The project's aspire.config.json should still exist with its original rich content.
        Assert.True(File.Exists(projectConfigPath),
            $"aspire.config.json in project directory should still exist: {projectConfigPath}");

        var currentContent = File.ReadAllText(projectConfigPath);

        // Verify the config was not modified by the run.
        Assert.Equal(originalContent, currentContent);

        using var doc = JsonDocument.Parse(currentContent);
        var root = doc.RootElement;

        // Verify appHost.path is "apphost.ts"
        Assert.True(root.TryGetProperty("appHost", out var appHost),
            $"aspire.config.json missing 'appHost' property. Content:\n{currentContent}");
        Assert.True(appHost.TryGetProperty("path", out var pathProp),
            $"aspire.config.json missing 'appHost.path'. Content:\n{currentContent}");
        Assert.Equal("apphost.ts", pathProp.GetString());

        // Verify language is typescript
        Assert.True(appHost.TryGetProperty("language", out var langProp),
            $"aspire.config.json missing 'appHost.language'. Content:\n{currentContent}");
        Assert.Contains("typescript", langProp.GetString(), StringComparison.OrdinalIgnoreCase);

        // Verify profiles section exists with applicationUrl
        Assert.True(root.TryGetProperty("profiles", out var profiles),
            $"aspire.config.json missing 'profiles' section. Content:\n{currentContent}");
        Assert.True(profiles.EnumerateObject().Any(),
            $"aspire.config.json 'profiles' section is empty. Content:\n{currentContent}");

        // At least one profile should have an applicationUrl
        var hasApplicationUrl = false;
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.TryGetProperty("applicationUrl", out _))
            {
                hasApplicationUrl = true;
                break;
            }
        }
        Assert.True(hasApplicationUrl,
            $"No profile has 'applicationUrl'. Content:\n{currentContent}");

        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
