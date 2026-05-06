// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests verifying configuration migration from legacy CLI config formats
/// (globalsettings.json, .aspire/settings.json) to the new unified aspire.config.json format.
/// These tests simulate the upgrade path users experience when updating from older CLI versions.
/// </summary>
/// <remarks>
/// Each test bind-mounts a host-side directory as <c>/root/.aspire/</c> in the container,
/// enabling direct host-side file creation and verification.
/// </remarks>
public sealed class ConfigMigrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Pin to the last CLI version before the aspire.config.json consolidation.
    /// When 13.2 ships with the config change, this version ensures the upgrade
    /// test exercises the real legacy-to-new migration path.
    /// </summary>
    private const string LegacyCliVersion = "13.1.0";

    /// <summary>
    /// Creates a Docker test terminal with a bind-mounted <c>~/.aspire/</c> directory
    /// for host-side file creation and verification.
    /// </summary>
    /// <returns>
    /// A tuple containing the host-side path to the mounted .aspire directory
    /// and the configured terminal.
    /// </returns>
    private (string AspireHomeDir, Hex1bTerminal Terminal) CreateMigrationTerminal(
        string repoRoot,
        CliInstallStrategy strategy,
        TemporaryWorkspace workspace,
        [System.Runtime.CompilerServices.CallerMemberName] string testName = "")
    {
        // Create a host-side directory that will be bind-mounted as /root/.aspire/ in the container.
        var aspireHomeDir = Path.Combine(workspace.WorkspaceRoot.FullName, "aspire-home");
        Directory.CreateDirectory(aspireHomeDir);

        var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot, strategy, output,
            workspace: workspace,
            additionalVolumes: [$"{aspireHomeDir}:/root/.aspire"],
            testName: testName);

        return (aspireHomeDir, terminal);
    }

    /// <summary>
    /// Throws if the file at <paramref name="filePath"/> does not contain all
    /// <paramref name="expectedStrings"/>.
    /// </summary>
    private static void AssertFileContains(string filePath, params string[] expectedStrings)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Expected file does not exist: {filePath}");
        }

        var content = File.ReadAllText(filePath);
        foreach (var expected in expectedStrings)
        {
            if (!content.Contains(expected))
            {
                throw new InvalidOperationException(
                    $"File {filePath} does not contain '{expected}'. Actual content:\n{content}");
            }
        }
    }

    /// <summary>
    /// Throws if the file at <paramref name="filePath"/> contains any of the
    /// <paramref name="unexpectedStrings"/>.
    /// </summary>
    private static void AssertFileDoesNotContain(string filePath, params string[] unexpectedStrings)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var content = File.ReadAllText(filePath);
        foreach (var unexpected in unexpectedStrings)
        {
            if (content.Contains(unexpected))
            {
                throw new InvalidOperationException(
                    $"File {filePath} unexpectedly contains '{unexpected}'. Actual content:\n{content}");
            }
        }
    }

    /// <summary>
    /// Verifies that a legacy ~/.aspire/globalsettings.json is automatically migrated to
    /// ~/.aspire/aspire.config.json when a CLI command is run, that the legacy file is
    /// preserved for backward compatibility with older CLI versions, and that the
    /// channel field is dropped during migration (channel is baked into the binary as
    /// AspireCliChannel assembly metadata; it is not stored in global config).
    /// </summary>
    [Fact]
    public async Task GlobalSettings_MigratedFromLegacyFormat()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Pre-populate legacy globalsettings.json on the host (visible in container via bind mount).
        var legacyPath = Path.Combine(aspireHomeDir, "globalsettings.json");
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");

        File.WriteAllText(legacyPath,
            """{"channel":"staging","features":{"polyglotSupportEnabled":true},"sdkVersion":"9.1.0"}""");

        // Ensure no aspire.config.json exists yet.
        if (File.Exists(newConfigPath))
        {
            File.Delete(newConfigPath);
        }

        // Run any CLI command to trigger global migration in Program.cs.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Migrated aspire.config.json retains non-channel content (features, sdkVersion).
        AssertFileContains(newConfigPath, "polyglotSupportEnabled");

        // Channel is intentionally dropped from the migrated global config.
        AssertFileDoesNotContain(newConfigPath, "\"channel\"", "staging");

        // Legacy file is preserved unchanged for backward compatibility, so it still
        // contains the original channel value.
        AssertFileContains(legacyPath, "channel", "staging");

        // aspire config get channel returns the not-found error and a non-zero exit
        // because the migrated global config has no channel key.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("not found", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForAnyPromptAsync(counter);

        // Cleanup.
        await auto.TypeAsync("aspire config delete features.polyglotSupportEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that global migration does NOT overwrite an existing aspire.config.json.
    /// If the user already has the new format, legacy globalsettings.json should be ignored.
    /// </summary>
    [Fact]
    public async Task GlobalMigration_SkipsWhenNewConfigExists()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Pre-populate BOTH files on the host: aspire.config.json with "preview",
        // globalsettings.json with "staging".
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");
        var legacyPath = Path.Combine(aspireHomeDir, "globalsettings.json");

        File.WriteAllText(newConfigPath,
            """{"channel":"preview"}""");
        File.WriteAllText(legacyPath,
            """{"channel":"staging","features":{"polyglotSupportEnabled":true}}""");

        // Run CLI. Migration should be skipped because aspire.config.json already exists.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify aspire.config.json still has "preview" (NOT overwritten with "staging").
        AssertFileContains(newConfigPath, "preview");
        AssertFileDoesNotContain(newConfigPath, "staging");

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that the CLI gracefully handles malformed JSON in legacy globalsettings.json
    /// without crashing, and that subsequent config operations still work.
    /// </summary>
    [Fact]
    public async Task GlobalMigration_HandlesMalformedLegacyJson()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Write malformed JSON to the legacy file.
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");

        File.WriteAllText(
            Path.Combine(aspireHomeDir, "globalsettings.json"),
            "this is not valid json {{{");

        if (File.Exists(newConfigPath))
        {
            File.Delete(newConfigPath);
        }

        // Run CLI. Should not crash despite malformed legacy file.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify CLI still works by setting and reading a value.
        await auto.TypeAsync("aspire config set channel stable -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("stable", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that legacy globalsettings.json containing JSON comments and trailing commas
    /// (common when hand-edited) is correctly parsed and migrated to aspire.config.json.
    /// </summary>
    [Fact]
    public async Task GlobalMigration_HandlesCommentsAndTrailingCommas()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Write legacy JSON with comments and trailing commas.
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");
        var legacyPath = Path.Combine(aspireHomeDir, "globalsettings.json");

        File.WriteAllText(legacyPath,
            """
            {
              // User-added comment
              "channel": "staging",
              "features": {
                "polyglotSupportEnabled": true,
              }
            }
            """);

        if (File.Exists(newConfigPath))
        {
            File.Delete(newConfigPath);
        }

        // Run CLI to trigger migration.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify migration succeeded despite comments/trailing commas (host-side).
        AssertFileContains(newConfigPath, "staging", "polyglotSupportEnabled");

        // Verify value accessible via config get.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("staging", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.polyglotSupportEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that aspire config set writes nested JSON to aspire.config.json
    /// (the new format) and that aspire config get correctly reads from this structure.
    /// Also confirms globalsettings.json is NOT created when using the new CLI.
    /// </summary>
    [Fact]
    public async Task ConfigSetGet_CreatesNestedJsonFormat()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Ensure clean state.
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");
        var legacyPath = Path.Combine(aspireHomeDir, "globalsettings.json");

        foreach (var f in new[] { newConfigPath, legacyPath })
        {
            if (File.Exists(f))
            {
                File.Delete(f);
            }
        }

        // Set nested config values.
        await auto.TypeAsync("aspire config set channel preview -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config set features.polyglotSupportEnabled true -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config set features.stagingChannelEnabled false -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify the file has nested JSON structure (host-side).
        AssertFileContains(newConfigPath, "features", "polyglotSupportEnabled", "preview");

        // Verify values are readable via aspire config get.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("preview", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get features.polyglotSupportEnabled");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("true", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify globalsettings.json was NOT created.
        if (File.Exists(legacyPath))
        {
            throw new InvalidOperationException(
                "globalsettings.json should not be created by the new CLI");
        }

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.polyglotSupportEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.stagingChannelEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Verifies that migration from globalsettings.json preserves all supported
    /// value types: channel (string), features (dictionary of bools), and packages
    /// (dictionary of strings).
    /// </summary>
    [Fact]
    public async Task GlobalMigration_PreservesAllValueTypes()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Create a comprehensive legacy globalsettings.json with all value types.
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");
        var legacyPath = Path.Combine(aspireHomeDir, "globalsettings.json");

        File.WriteAllText(legacyPath,
            """
            {
                "channel": "preview",
                "sdkVersion": "9.1.0",
                "features": {
                    "polyglotSupportEnabled": true,
                    "stagingChannelEnabled": false
                },
                "packages": {
                    "Aspire.Hosting.Redis": "9.1.0"
                }
            }
            """);

        if (File.Exists(newConfigPath))
        {
            File.Delete(newConfigPath);
        }

        // Trigger migration.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify all value types were migrated (host-side).
        AssertFileContains(newConfigPath,
            "preview",
            "polyglotSupportEnabled",
            "stagingChannelEnabled",
            "Aspire.Hosting.Redis");

        // Verify individual value via config get.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("preview", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.polyglotSupportEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.stagingChannelEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete packages -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }

    /// <summary>
    /// Full end-to-end upgrade test: installs a released (legacy) CLI version that
    /// predates the aspire.config.json consolidation, sets global config values using the
    /// old format, then upgrades to the new CLI and verifies all settings are migrated.
    /// </summary>
    [Fact]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/15937")]
    [OuterloopTest("Requires downloading two separate CLI versions from GitHub")]
    public async Task FullUpgrade_LegacyCliToNewCli_MigratesGlobalSettings()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        var (aspireHomeDir, terminal) = CreateMigrationTerminal(repoRoot, strategy, workspace);
        using var _ = terminal;
        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);

        // Step 1: Install a released CLI that uses the legacy config format.
        await auto.InstallAspireCliVersionAsync(LegacyCliVersion, counter);
        await auto.SourceAspireCliEnvironmentAsync(counter);

        // Verify the legacy CLI is installed.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 2: Set global values using the legacy CLI.
        // In versions before 13.2, this writes to ~/.aspire/globalsettings.json.
        await auto.TypeAsync("aspire config set channel staging -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config set features.polyglotSupportEnabled true -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Verify values were persisted by the legacy CLI.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("staging", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Snapshot which files exist after using the legacy CLI (for debugging).
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("ls -la ~/.aspire/");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 3: Install the new CLI (from this PR), overwriting the legacy CLI.
        await auto.InstallAspireCliAsync(strategy, counter);

        // Step 4: Run the new CLI to trigger global migration.
        await auto.TypeAsync("aspire --version");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        // Step 5: Verify aspire.config.json exists with migrated values (host-side).
        var newConfigPath = Path.Combine(aspireHomeDir, "aspire.config.json");

        AssertFileContains(newConfigPath, "staging", "polyglotSupportEnabled");

        // Step 6: Verify values are still accessible via the new CLI.
        await auto.ClearScreenAsync(counter);
        await auto.TypeAsync("aspire config get channel");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("staging", timeout: TimeSpan.FromSeconds(10));
        await auto.WaitForSuccessPromptAsync(counter);

        // Cleanup.
        await auto.TypeAsync("aspire config delete channel -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("aspire config delete features.polyglotSupportEnabled -g");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;
    }
}
