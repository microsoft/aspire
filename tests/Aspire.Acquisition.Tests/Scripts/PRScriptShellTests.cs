// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the bash PR script (get-aspire-cli-pr.sh).
/// These tests validate parameter handling with mock gh CLI.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class PRScriptShellTests(ITestOutputHelper testOutput)
{
    private static readonly string s_scriptPath = ScriptPaths.PRShell;
    private readonly ITestOutputHelper _testOutput = testOutput;

    private async Task<ScriptToolCommand> CreateCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AllMainParameters_ShownInHelp()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("--run-id", result.Output);
        Assert.Contains("--local-dir", result.Output);
        Assert.Contains("--hive-label", result.Output);
        Assert.Contains("--install-path", result.Output);
        Assert.Contains("--os", result.Output);
        Assert.Contains("--arch", result.Output);
        Assert.Contains("--hive-only", result.Output);
        Assert.Contains("--skip-extension", result.Output);
        Assert.Contains("--use-insiders", result.Output);
        Assert.Contains("--skip-path", result.Output);
        Assert.Contains("--keep-archive", result.Output);
        Assert.Contains("--verbose", result.Output);
        Assert.Contains("--dry-run", result.Output);
    }

    [Fact]
    public async Task MissingPRNumberAndRunId_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("PR number", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--run-id", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunWithPRNumber_ShowsSteps()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("12345", result.Output);
        Assert.Contains("[DRY RUN]", result.Output);
    }

    [Fact]
    public async Task CustomInstallPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--install-path", customPath);

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task RunIdParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--run-id", "987654321");

        result.EnsureSuccessful();
        Assert.Contains("987654321", result.Output);
    }

    [Fact]
    public async Task LocalDir_DryRun_UsesLocalDirectoryWithoutGh()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local artifacts");
        Directory.CreateDirectory(localDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-preview.1.12345.1");

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--hive-label", "test-hive",
            "--hive-only",
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains("Installing from local directory", result.Output);
        Assert.Contains(localDir, result.Output);
        Assert.Contains("test-hive", result.Output);
        Assert.DoesNotContain("Downloading CLI from GitHub", result.Output);
    }

    [Fact]
    public async Task RunIdAsFirstArg_DryRun_Succeeds()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--run-id", "987654321", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("987654321", result.Output);
        Assert.Contains("[DRY RUN]", result.Output);
    }

    [Fact]
    public async Task RunIdAsFirstArg_DryRun_UsesRunHiveLabel()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--run-id", "987654321", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("run-987654321", result.Output);
    }

    [Fact]
    public async Task RunIdWithPRNumber_DryRun_UsesPrHiveLabel()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--run-id", "987654321", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("pr-12345", result.Output);
    }

    [Fact]
    public async Task RunIdAsFirstArg_NonNumericValue_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--run-id", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Run ID must be a number", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunIdAsFirstArg_NoValue_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--run-id");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ShortRunIdFlag_AsFirstArg_DryRun_Succeeds()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-r", "987654321", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("987654321", result.Output);
    }

    [Fact]
    public async Task OSOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--os", "linux");

        result.EnsureSuccessful();
        Assert.Contains("linux", result.Output);
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--arch", "x64");

        result.EnsureSuccessful();
        Assert.Contains("x64", result.Output);
    }

    [Fact]
    public async Task HiveOnlyFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--hive-only");

        result.EnsureSuccessful();
        Assert.Contains("hive-only", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkipExtensionFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--skip-extension");

        result.EnsureSuccessful();
        Assert.Contains("skip-extension", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("Skipping PATH", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("--use-insiders")]
    [InlineData("--verbose")]
    [InlineData("--keep-archive")]
    public async Task BooleanFlags_AreAccepted(string flag)
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", flag);

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task MultipleFlags_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--dry-run",
            "--install-path", customPath,
            "--os", "linux",
            "--arch", "x64",
            "--skip-extension",
            "--skip-path",
            "--keep-archive",
            "--verbose");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
        Assert.Contains("[DRY RUN]", result.Output);
    }

    [Fact]
    public async Task InvalidPRNumber_NonNumeric_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("abc", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task InvalidPRNumber_Zero_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("0", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task InvalidPRNumber_Negative_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-1", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task UnrecognizedOptionAsFirstArg_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task UnknownFlag_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--nonexistent-flag", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown option", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AspireRepoEnvVar_IsUsedInDryRun()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);
        cmd.WithEnvironmentVariable("ASPIRE_REPO", "my-org/my-aspire");

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("my-org/my-aspire", result.Output);
    }

    [Fact]
    public async Task DryRun_ShowsArtifactNameWithRid()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "--dry-run", "--skip-path", "--verbose",
            "--os", "linux", "--arch", "x64");

        result.EnsureSuccessful();
        Assert.Contains("cli-native-archives", result.Output);
    }

    [Fact]
    public async Task DryRun_ShowsDefaultInstallPath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains(".aspire", result.Output);
    }

    [Fact]
    public async Task DryRun_ShowsNugetHivePath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("hive", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HiveOnly_SkipsCLIDownload()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--dry-run", "--skip-path", "--hive-only", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("Skipping CLI download", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // PR2-S11(c)(i): PR-route CLI binary lands at <prefix>/dogfood/pr-<N>/bin so PR installs
    // do not collide with the script-route prefix (<prefix>/bin) or with other PR installs.
    [Fact]
    public async Task DryRun_PRRoute_CliInstallPath_IsUnderDogfoodPrN()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var expectedPathSegment = Path.Combine("dogfood", "pr-99999", "bin");
        Assert.Contains(expectedPathSegment, result.Output);
        var scriptRouteBin = Path.Combine(env.MockHome, ".aspire", "bin") + Path.DirectorySeparatorChar;
        Assert.DoesNotContain($"Would install CLI archive to: {scriptRouteBin}\n", result.Output);
    }

    // PR2-S11(c)(ii): PR-route sidecar at <prefix>/dogfood/pr-<N>/.aspire-install.json with
    // route="pr" and updateCommand naming the script + PR number. Written under --dry-run via
    // raw printf so callers (including this test) can observe the file.
    [Fact]
    public async Task DryRun_PRRoute_WritesSidecarWithCorrectContent()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected PR-route sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.Equal("pr", doc.RootElement.GetProperty("route").GetString());
        var updateCommand = doc.RootElement.GetProperty("updateCommand").GetString();
        Assert.NotNull(updateCommand);
        Assert.Contains("get-aspire-cli-pr.sh", updateCommand);
        Assert.Contains("-r 99999", updateCommand);
    }

    // PR2-S11(c)(iii): PR-route install prints the PATH-activation hint to stdout so users
    // know how to add <prefix>/dogfood/pr-<N>/bin to their shell profile.
    [Fact]
    public async Task DryRun_PRRoute_PrintsPathHintToStdout()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("Add to your shell profile:", result.Output);
        Assert.Contains("export PATH=", result.Output);
        Assert.Contains(Path.Combine("dogfood", "pr-99999", "bin"), result.Output);
    }

    // PR2-S11(c)(iv): PR-route hive location is unchanged at <prefix>/hives/pr-<N>/packages.
    // The path relocation in PR2-S5 only touched the CLI binary install path.
    [Fact]
    public async Task DryRun_PRRoute_HiveLocation_IsUnchanged()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        var expectedHive = Path.Combine("hives", "pr-99999", "packages");
        Assert.Contains(expectedHive, result.Output);
    }

    // PR2-S11(d): PR-route sidecar carries route metadata only — never a "channel" key.
    [Fact]
    public async Task DryRun_PRRouteSidecar_DoesNotContainChannelKey()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.False(
            doc.RootElement.TryGetProperty("channel", out _),
            $"Sidecar at {sidecarPath} unexpectedly contains a 'channel' key. Content: {sidecarContent}");
    }

    // PR2-S11(d) companion: under --dry-run no global aspire.config.json is materialized.
    [Fact]
    public async Task DryRun_PRRoute_DoesNotCreateGlobalAspireConfigJson()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(File.Exists(globalConfig), $"Unexpected global config at {globalConfig}");
    }

    // PR2-TG1: install_from_local_dir codepath — gap fix coverage. Local-dir installs land at
    // <prefix>/bin and must write a script-route sidecar so InstallPathResolver can identify
    // them. Before the gap fix, this test FAILED (sidecar was never written for local-dir).
    [Fact]
    public async Task DryRun_LocalDir_WritesScriptRouteSidecar_AtPrefixRoot()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local-artifacts");
        Directory.CreateDirectory(localDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-preview.1.12345.1");
        // The CLI archive isn't unpacked under --dry-run, but install_from_local_dir requires
        // exactly one matching archive to be discoverable for the configured RID.
        var rid = OperatingSystem.IsMacOS() ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : OperatingSystem.IsLinux() ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64")
                : "win-x64";
        await FakeArchiveHelper.CreateFakeArchiveAsync(localDir, rid);

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--hive-label", "test-hive",
            "--skip-path",
            "--dry-run");

        result.EnsureSuccessful();

        // Local-dir installs go under <prefix>/bin (no PR_NUMBER), so the sidecar lives at
        // the prefix root — same place as get-aspire-cli.sh writes it.
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected script-route sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.Equal("script", doc.RootElement.GetProperty("route").GetString());
    }
}
