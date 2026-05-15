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
    public async Task LocalDir_DryRun_AutoDetectsRawBuildOutput()
    {
        // When --local-dir points at a directory that contains only a raw 'aspire' executable
        // (no aspire-cli-*.tar.gz/.zip archive), auto-detect must dispatch to the raw-build flow
        // (install_aspire_cli_from_binary) instead of erroring on a missing archive.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "raw-build");
        Directory.CreateDirectory(localDir);
        // Drop a fake 'aspire' executable — no archive, no nupkgs.
        await File.WriteAllTextAsync(Path.Combine(localDir, "aspire"), "#!/bin/sh\necho fake\n");

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--hive-label", "test-hive",
            "--dry-run",
            "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("Installing from local directory", result.Output);
        Assert.Contains("[DRY RUN] Would install raw CLI binary", result.Output);
        Assert.DoesNotContain("Downloading CLI from GitHub", result.Output);
    }

    [Fact]
    public async Task RunIdAsFirstArg_DryRun_Succeeds()
    {
        // Verifies --run-id is recognized as a first-positional flag. Pair with
        // --hive-label because --run-id alone is rejected (the installed CLI's
        // channel is baked at build time and never points at a 'run-<id>' hive).
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--run-id", "987654321", "--hive-label", "pr-12345", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("987654321", result.Output);
        Assert.Contains("[DRY RUN]", result.Output);
    }

    [Fact]
    public async Task RunIdOnly_NoHiveLabel_DryRun_Rejected()
    {
        // Regression guard: --run-id alone (without --pr-number / --hive-label) must
        // fail with actionable guidance. The installed CLI's channel is baked at build
        // time (pr-<N>/staging/daily/local); 'run-<id>' is never a baked channel, so a
        // hives/run-<id>/packages layout would be unreachable from the CLI.
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("--run-id", "987654321", "--dry-run", "--skip-path", "--verbose");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cannot determine hive label from --run-id alone", result.Output);
        Assert.Contains("--pr-number", result.Output);
        Assert.Contains("--hive-label", result.Output);
        Assert.DoesNotContain("run-987654321", result.Output);
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
        // Verifies -r is recognized as a first-positional alias for --run-id. Pair
        // with --hive-label because --run-id alone is rejected.
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-r", "987654321", "--hive-label", "pr-12345", "--dry-run", "--skip-path");

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

    [Fact]
    public async Task LocalDir_DryRun_WithGitHubRunIdEnvSet_UsesLocalHiveLabel()
    {
        // Regression guard: the GITHUB_RUN_ID env var must NOT influence the hive label
        // when --local-dir is used. Without this test, re-introducing a GITHUB_RUN_ID
        // branch would produce "run-99999" silently instead of the expected "local"
        // hive label.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local-artifacts");
        Directory.CreateDirectory(localDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-local.1");

        // Inject GITHUB_RUN_ID only into the launched process — not the test process environment.
        cmd.WithEnvironmentVariable("GITHUB_RUN_ID", "99999");

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--hive-only",
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains("Using hive label: local", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("run-99999", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // PR-route CLI binary lands at <prefix>/dogfood/pr-<N>/bin so PR installs
    // do not collide with the script-route prefix (<prefix>/bin) or with other PR installs.
    // Under --dry-run the script emits the absolute install path via a
    // "DRYRUN: would install Aspire CLI binary to: <path>" message on stdout;
    // this test parses that message to verify the resolved install path.
    [Fact]
    public async Task DryRun_PRRoute_CliInstallPath_IsUnderDogfoodPrN()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var expectedBinaryPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", "aspire");
        Assert.Contains($"DRYRUN: would install Aspire CLI binary to: {expectedBinaryPath}", result.Output);
        var scriptRouteBin = Path.Combine(env.MockHome, ".aspire", "bin", "aspire");
        Assert.DoesNotContain($"DRYRUN: would install Aspire CLI binary to: {scriptRouteBin}", result.Output);
    }

    // Under --dry-run the PR-route script must NOT write the source=pr sidecar
    // at <prefix>/dogfood/pr-<N>/.aspire-install.json. The describe-but-do-not-do
    // contract requires the script to print a DRYRUN message naming the path it
    // would write, then return without touching the filesystem. Positive sidecar
    // content (the source field) for the PR route is covered by end-to-end
    // install runs, not at this unit-test layer.
    [Fact]
    public async Task DryRun_PRRoute_DoesNotWriteSidecar_AndAnnouncesPath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write route sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }

    // PR-route install prints the PATH-activation hint to stdout so users
    // know how to add <prefix>/dogfood/pr-<N>/bin to their shell profile.
    //
    // Hint must carry the literal "$HOME" form, not the pre-expanded absolute path,
    // so the profile line is portable across users.
    [Fact]
    public async Task DryRun_PRRoute_PrintsPathHintToStdout()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var hintLine = ExtractShellProfileHintLine(result.Output);
        Assert.Contains("export PATH=", hintLine);
        Assert.Contains(Path.Combine("dogfood", "pr-99999", "bin"), hintLine);
        Assert.Contains("$HOME/.aspire/dogfood/pr-99999/bin", hintLine);
        Assert.DoesNotContain(env.MockHome, hintLine);
    }

    [Fact]
    public async Task DryRun_PRRoute_PrintsPathHintWithAbsoluteInstallPath()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom-prefix");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path", "--install-path", customPath);

        result.EnsureSuccessful();
        var hintLine = ExtractShellProfileHintLine(result.Output);
        Assert.Contains($"{customPath}/dogfood/pr-99999/bin", hintLine);
        Assert.DoesNotContain("$HOME", hintLine);
    }

    private static string ExtractShellProfileHintLine(string output)
    {
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Contains("Add to your shell profile:", StringComparison.Ordinal))
            {
                return line;
            }
        }

        Assert.Fail($"Expected an 'Add to your shell profile:' line in output, but none was emitted. Output was:\n{output}");
        return string.Empty;
    }

    // PR-route hive location is unchanged at <prefix>/hives/pr-<N>/packages.
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

    // The PR-route script must not mutate route sidecars under --dry-run.
    // Companion to DryRun_PRRoute_DoesNotWriteSidecar_AndAnnouncesPath; kept as a
    // separate test method to keep dry-run sidecar-absence coverage visible in
    // test inventories alongside the other dry-run guards.
    [Fact]
    public async Task DryRun_PRRoute_DoesNotWriteSidecar_AnyContentRegression()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", ".aspire-install.json");
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }

    // Under --dry-run no global aspire.config.json is materialized.
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

    // Spec: --local-dir installs are unmanaged. The CLI artifacts come from a directory
    // the user already has — there is no self-update path. The script MUST therefore NOT
    // write a sidecar for the install: a sidecar would falsely advertise a managed
    // source. With no sidecar, downstream commands (e.g. `aspire update --self`) refuse
    // to assume any update channel.
    [Fact]
    public async Task DryRun_LocalDir_DoesNotWriteSidecar()
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

        // The new canonical sidecar position is <prefix>/bin/.aspire-install.json.
        // --local-dir without a PR number is an unmanaged install, so the script must
        // not write a sidecar; the resolver should return Unknown for these installs.
        var binSidecar = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.False(File.Exists(binSidecar), $"--local-dir install must not write sidecar at {binSidecar} (unmanaged route).");

        // Defensive: walk the .aspire root and assert no sidecar landed at any depth.
        var aspireRoot = Path.Combine(env.MockHome, ".aspire");
        if (Directory.Exists(aspireRoot))
        {
            var anySidecar = Directory.GetFiles(aspireRoot, ".aspire-install.json", SearchOption.AllDirectories);
            Assert.Empty(anySidecar);
        }
    }

    // PR_NUMBER input validation — empty string. The first positional arg must be a
    // valid PR number, --run-id, or --local-dir. An empty string is none of those.
    [Fact]
    public async Task EmptyPRNumber_ReturnsError_AndCreatesNoFiles()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    // Very large PR number above int.MaxValue. Bash regex ^[1-9][0-9]*$ accepts
    // any digit-only string so the script proceeds. Documented behavior: there is no upper
    // bound on PR_NUMBER bash-side; the path segment is constructed safely (digits only).
    // Under --dry-run the script announces the sidecar path it would write (but does not
    // write it); this test parses that announcement to verify the resolved path.
    [Fact]
    public async Task VeryLargePRNumber_AcceptedByScript_DryRunAnnouncesSidecarPath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("99999999999", "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999999999", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write route sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }

    // Path-traversal / command-injection in PR_NUMBER must be rejected at
    // parse time so it never reaches the path-construction code. The regex ^[1-9][0-9]*$ is
    // the gate; this test verifies the gate holds and no files leak under <prefix>/dogfood.
    [Theory]
    [InlineData("../etc")]
    [InlineData("../../tmp")]
    [InlineData("..")]
    [InlineData("12345; rm -rf /tmp")]
    [InlineData("12345 hello")]
    [InlineData("12345|cat")]
    [InlineData("12345&true")]
    [InlineData("12345`whoami`")]
    [InlineData("$(whoami)")]
    public async Task SpecialCharsPRNumber_Rejected_AndCreatesNoFiles(string pr)
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(pr, "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    // Without the PR_NUMBER regex, "-1" would land in dogfood/pr--1/ and
    // "00001"/"0123" would all collapse onto dogfood/pr-1/.
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("00001")]
    [InlineData("0123")]
    public async Task LeadingZeroOrNegativePRNumber_Rejected_AndCreatesNoFiles(string pr)
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(pr, "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    private static void AssertNoDogfoodInstall(string mockHome)
    {
        var dogfoodRoot = Path.Combine(mockHome, ".aspire", "dogfood");
        if (Directory.Exists(dogfoodRoot))
        {
            var leaks = Directory.GetFileSystemEntries(dogfoodRoot, "*", SearchOption.AllDirectories);
            Assert.True(leaks.Length == 0, $"Unexpected files under {dogfoodRoot}: {string.Join(", ", leaks)}");
        }
    }
}
