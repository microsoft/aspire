// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the PowerShell PR script (get-aspire-cli-pr.ps1).
/// These tests validate parameter handling using -WhatIf (not -DryRun).
/// PowerShell scripts support -WhatIf as the dry-run equivalent.
/// The mock gh CLI uses top-level goto dispatch on Windows to avoid
/// CMD issues with exit /b inside nested if () blocks.
/// </summary>
[RequiresTools(["pwsh"])]
public class PRScriptPowerShellTests(ITestOutputHelper testOutput)
{
    private static readonly string s_scriptPath = ScriptPaths.PRPowerShell;
    private readonly ITestOutputHelper _testOutput = testOutput;

    private async Task<ScriptToolCommand> CreateCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    [Fact]
    public async Task GetHelp_WithQuestionMark_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-?");

        result.EnsureSuccessful();
        Assert.True(
            result.Output.Contains("SYNOPSIS", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("PARAMETERS", StringComparison.OrdinalIgnoreCase),
            "Output should contain help information");
    }

    [Fact]
    public async Task AllMainParameters_ShownInHelp()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-?");

        result.EnsureSuccessful();

        // PowerShell help wraps long lines, which can split parameter names across lines
        // (e.g., "PRNumb\n    er]"). Normalize by removing newlines and continuation whitespace.
        var normalized = System.Text.RegularExpressions.Regex.Replace(result.Output, @"\r?\n\s*", "");

        Assert.Contains("PRNumber", normalized);
        Assert.Contains("LocalDir", normalized);
        Assert.Contains("HiveLabel", normalized);
        Assert.Contains("InstallPath", normalized);
        Assert.Contains("OS", normalized);
        Assert.Contains("Architecture", normalized);
        Assert.Contains("HiveOnly", normalized);
        Assert.Contains("SkipExtension", normalized);
        Assert.Contains("UseInsiders", normalized);
        Assert.Contains("SkipPath", normalized);
        Assert.Contains("KeepArchive", normalized);
    }

    [Fact]
    public async Task MissingPRNumber_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("PRNumber", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIfWithPRNumber_ShowsSteps()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("12345", result.Output);
    }

    [Fact]
    public async Task CustomInstallPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-InstallPath", customPath, "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task RunIdParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-WorkflowRunId", "987654321", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("987654321", result.Output);
    }

    [Fact]
    public async Task WorkflowRunIdOnly_NoHiveLabel_WhatIf_Rejected()
    {
        // Regression guard: -WorkflowRunId alone (without -PRNumber / -HiveLabel) must
        // fail with actionable guidance. The installed CLI's channel is baked at build
        // time (pr-<N>/staging/daily/local); 'run-<id>' is never a baked channel, so a
        // hives/run-<id>/packages layout would be unreachable from the CLI.
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-WorkflowRunId", "987654321", "-WhatIf", "-SkipPath", "-Verbose");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cannot determine hive label from -WorkflowRunId alone", result.Output);
        Assert.Contains("-PRNumber", result.Output);
        Assert.Contains("-HiveLabel", result.Output);
        Assert.DoesNotContain("run-987654321", result.Output);
    }

    [Fact]
    public async Task LocalDir_WhatIf_UsesLocalDirectoryWithoutGh()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local artifacts");
        Directory.CreateDirectory(localDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-preview.1.12345.1");

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-HiveLabel", "test-hive",
            "-HiveOnly",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Installing from local directory", result.Output);
        Assert.Contains(localDir, result.Output);
        Assert.Contains("test-hive", result.Output);
        Assert.DoesNotContain("Downloading CLI from GitHub", result.Output);
    }

    [Fact]
    public async Task LocalDir_WhatIf_AutoDetectsRawBuildOutput()
    {
        // When -LocalDir points at a directory that contains only a raw 'aspire' executable
        // (no aspire-cli-*.tar.gz/.zip archive), auto-detect must dispatch to the raw-build flow
        // (Install-AspireCliFromLocalBinary) instead of erroring on a missing archive.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "raw-build");
        Directory.CreateDirectory(localDir);
        // Drop a fake 'aspire' executable — no archive, no nupkgs.
        await File.WriteAllTextAsync(Path.Combine(localDir, "aspire"), "#!/bin/sh\necho fake\n");

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-HiveLabel", "test-hive",
            "-WhatIf",
            "-SkipPath");

        result.EnsureSuccessful();
        Assert.Contains("Installing from local directory", result.Output);
        // WhatIf preview from Install-AspireCliFromLocalBinary's ShouldProcess message
        // (or the script's error if dispatch is wrong) — we expect the raw-binary path.
        Assert.Contains("raw Aspire CLI binary tree", result.Output);
        Assert.DoesNotContain("Downloading CLI from GitHub", result.Output);
    }

    [Fact]
    public async Task OSOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-OS", "win", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("win", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-Architecture", "x64", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("x64", result.Output);
    }

    [Fact]
    public async Task HiveOnlyFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-HiveOnly", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("HiveOnly", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkipExtensionFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-SkipExtension", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("SkipExtension", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Skipping PATH", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleFlags_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-PRNumber", "12345",
            "-InstallPath", customPath,
            "-OS", "linux",
            "-Architecture", "arm64",
            "-SkipExtension",
            "-SkipPath",
            "-KeepArchive",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task InvalidPRNumber_NonNumeric_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "abc", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task InvalidPRNumber_Zero_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "0", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task InvalidPRNumber_Negative_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "-1", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task DryRun_ShowsDefaultInstallPath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(".aspire", result.Output);
    }

    [Fact]
    public async Task AspireRepoEnvVar_IsUsedInDryRun()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);
        cmd.WithEnvironmentVariable("ASPIRE_REPO", "my-org/my-aspire");

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-Verbose", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("my-org/my-aspire", result.Output);
    }

    [Fact]
    public async Task DryRun_ShowsArtifactNameWithRid()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-PRNumber", "12345",
            "-OS", "linux",
            "-Architecture", "x64",
            "-Verbose",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("cli-native", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRun_ShowsNugetHivePath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-Verbose", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("hive", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HiveOnly_SkipsCLIDownload()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-HiveOnly", "-Verbose", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Skipping CLI download", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LocalDir_WhatIf_WithGitHubRunIdEnvSet_UsesLocalHiveLabel()
    {
        // Regression guard: the GITHUB_RUN_ID env var must NOT influence the hive label
        // when -LocalDir is used. Without this test, re-introducing a GITHUB_RUN_ID
        // branch would produce "run-99999" silently instead of the expected "local"
        // hive label.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local-artifacts");
        Directory.CreateDirectory(localDir);
        // Non-PR-style version ensures auto-detect falls through to "local" label.
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-dev.1");

        // Inject GITHUB_RUN_ID only into the launched process — not the test process environment.
        cmd.WithEnvironmentVariable("GITHUB_RUN_ID", "99999");

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-HiveOnly",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Using hive label: local", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("run-99999", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // PR-route CLI binary lands at <prefix>/dogfood/pr-<N>/bin so PR installs
    // do not collide with the script-route prefix or with other PR installs.
    // Under -WhatIf the script emits the absolute install path via a
    // "What if: Aspire CLI binary would be installed to: <path>" message on stdout
    // (PS-native WhatIf style); this test parses that message to verify the
    // resolved install path.
    [Fact]
    public async Task WhatIf_PRRoute_CliInstallPath_IsUnderDogfoodPrN()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var binaryName = OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
        var expectedBinaryPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", binaryName);
        Assert.Contains($"What if: Aspire CLI binary would be installed to: {expectedBinaryPath}", result.Output);
    }

    // Under -WhatIf the PR-route script must NOT write the source=pr sidecar
    // at <prefix>/dogfood/pr-<N>/.aspire-install.json. The describe-but-do-not-do
    // contract requires the script to print a "What if:" message naming the path
    // it would write, then return without touching the filesystem. Positive
    // sidecar content (the source field) for the PR route is covered by
    // end-to-end install runs, not at this unit-test layer.
    [Fact]
    public async Task WhatIf_PRRoute_DoesNotWriteSidecar_AndAnnouncesPath()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", ".aspire-install.json");
        Assert.Contains($"What if: Route sidecar would be written to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under -WhatIf, but found one at {sidecarPath}");
    }

    // PR-route install prints the PATH-activation hint via Write-Host. The
    // OS path separator keeps the line valid on both Windows (;) and Unix (:).
    // The new-PATH expression must be double-quoted so PowerShell expands
    // `$env:PATH` when the user pastes the line into their profile — single
    // quotes would assign the literal text and clobber PATH.
    //
    // Separator before `$env:PATH` must be [System.IO.Path]::PathSeparator — a
    // hard-coded ":" or ";" breaks the hint on the opposite platform.
    [Fact]
    public async Task WhatIf_PRRoute_PrintsPathHintToStdout()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var hintLine = ExtractShellProfileHintLine(result.Output);
        Assert.Contains("$env:PATH", hintLine);
        Assert.Contains(Path.Combine("dogfood", "pr-99999", "bin"), hintLine);
        var binDir = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin");
        // The assignment value must be wrapped in double quotes so that
        // $env:PATH expands at user-paste time. Single quotes would make the
        // user's PATH become the literal string "...bin;$env:PATH".
        Assert.Contains($"\"{binDir}", hintLine);
        Assert.DoesNotContain($"'{binDir}", hintLine);
        // Separator joining the new bin dir to `$env:PATH` must come from
        // [System.IO.Path]::PathSeparator, not a hard-coded ":" / ";".
        Assert.Contains($"{binDir}{Path.PathSeparator}$env:PATH", hintLine);
    }

    [Fact]
    public async Task WhatIf_PRRoute_PrintsPathHintWithAbsoluteInstallPath()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom-prefix");
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf", "-InstallPath", customPath);

        result.EnsureSuccessful();
        var hintLine = ExtractShellProfileHintLine(result.Output);
        var binDir = Path.Combine(customPath, "dogfood", "pr-99999", "bin");
        Assert.Contains($"\"{binDir}", hintLine);
        Assert.Contains($"{binDir}{Path.PathSeparator}$env:PATH", hintLine);
        Assert.DoesNotContain(Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin"), hintLine);
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
    public async Task WhatIf_PRRoute_HiveLocation_IsUnchanged()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-Verbose", "-WhatIf");

        result.EnsureSuccessful();
        var expectedHive = Path.Combine("hives", "pr-99999", "packages");
        Assert.Contains(expectedHive, result.Output);
    }

    // The PR-route script must not mutate route sidecars under -WhatIf.
    // Companion to WhatIf_PRRoute_DoesNotWriteSidecar_AndAnnouncesPath; kept as a
    // separate test method to keep -WhatIf sidecar-absence coverage visible in
    // test inventories alongside the other -WhatIf guards.
    [Fact]
    public async Task WhatIf_PRRoute_DoesNotWriteSidecar_AnyContentRegression()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", "bin", ".aspire-install.json");
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under -WhatIf, but found one at {sidecarPath}");
    }

    // Under -WhatIf no global aspire.config.json is created.
    [Fact]
    public async Task WhatIf_PRRoute_DoesNotCreateGlobalAspireConfigJson()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(File.Exists(globalConfig), $"Unexpected global config at {globalConfig}");
    }

    // Spec: --local-dir / -LocalDir installs are unmanaged. The CLI artifacts come from
    // a directory the user already has — there is no self-update path. The script MUST
    // therefore NOT write a sidecar for the install: a sidecar would falsely advertise a
    // managed source. With no sidecar, downstream commands (e.g. `aspire update --self`)
    // refuse to assume any update channel.
    [Fact]
    public async Task WhatIf_LocalDir_DoesNotWriteSidecar()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var localDir = Path.Combine(env.TempDirectory, "local-artifacts");
        Directory.CreateDirectory(localDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(localDir, "Aspire.Cli", "13.3.0-preview.1.12345.1");
        var rid = OperatingSystem.IsMacOS() ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : OperatingSystem.IsLinux() ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64")
                : "win-x64";
        await FakeArchiveHelper.CreateFakeArchiveAsync(localDir, rid);

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-HiveLabel", "test-hive",
            "-SkipPath",
            "-WhatIf");

        result.EnsureSuccessful();

        // The new canonical sidecar position is <prefix>/bin/.aspire-install.json.
        // -LocalDir without a PR number is an unmanaged install, so the script must
        // not write a sidecar; the resolver should return Unknown for these installs.
        var binSidecar = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.False(File.Exists(binSidecar), $"--local-dir install must not write sidecar at {binSidecar} (unmanaged route).");

        // Defensive: assert no .aspire-install.json anywhere under the install root.
        var aspireRoot = Path.Combine(env.MockHome, ".aspire");
        if (Directory.Exists(aspireRoot))
        {
            var anySidecar = Directory.GetFiles(aspireRoot, ".aspire-install.json", SearchOption.AllDirectories);
            Assert.Empty(anySidecar);
        }
    }

    // PR_NUMBER input validation — empty string. PowerShell parameter binding
    // rejects empty strings for [int] parameters before any path construction occurs.
    [Fact]
    public async Task EmptyPRNumber_ReturnsError_AndCreatesNoFiles()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    // Very large PR number above [int]::MaxValue (2147483647). PowerShell's [int]
    // parameter binding fails the cast — the script must reject and create no files.
    [Fact]
    public async Task VeryLargePRNumber_AboveIntMax_Rejected_AndCreatesNoFiles()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999999999", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    // Non-numeric / special-char PR_NUMBER is rejected at parameter
    // binding ([int] cast or ValidateRange). This guards against path injection / command
    // injection routes via the PR_NUMBER value.
    [Theory]
    [InlineData("../etc")]
    [InlineData("..")]
    [InlineData("12345 hello")]
    [InlineData("12345;rm")]
    [InlineData("12345|cat")]
    [InlineData("$(whoami)")]
    public async Task SpecialCharsPRNumber_Rejected_AndCreatesNoFiles(string pr)
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", pr, "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        AssertNoDogfoodInstall(env.MockHome);
    }

    // PowerShell binds -PRNumber as [int] with [ValidateRange(1, int.MaxValue)], so
    // zero and negative values fail validation. Leading-zero strings like "00001" are
    // accepted because PowerShell applies the [int] cast before parameter-validation
    // attributes; the bash counterpart enforces the leading-zero contract via regex
    // (see PRScriptShellTests.LeadingZeroOrNegativePRNumber_Rejected_AndCreatesNoFiles).
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task NonPositivePRNumber_Rejected_AndCreatesNoFiles(string pr)
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", pr, "-WhatIf");

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
