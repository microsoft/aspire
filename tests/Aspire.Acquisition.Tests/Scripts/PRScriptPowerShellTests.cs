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

    // PR-route CLI binary lands at <prefix>/dogfood/pr-<N>/bin so PR installs
    // do not collide with the script-route prefix or with other PR installs.
    [Fact]
    public async Task WhatIf_PRRoute_CliInstallPath_IsUnderDogfoodPrN()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var expectedPathSegment = Path.Combine("dogfood", "pr-99999", "bin");
        Assert.Contains(expectedPathSegment, result.Output);
    }

    // PR-route sidecar at <prefix>/dogfood/pr-<N>/.aspire-install.json with
    // route="pr" and updateCommand naming the script + PR number. Written under -WhatIf via
    // raw .NET I/O so callers (including this test) can observe the file even though
    // PowerShell ShouldProcess-aware cmdlets silently no-op.
    [Fact]
    public async Task WhatIf_PRRoute_WritesSidecarWithCorrectContent()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected PR-route sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.Equal("pr", doc.RootElement.GetProperty("route").GetString());
        var updateCommand = doc.RootElement.GetProperty("updateCommand").GetString();
        Assert.NotNull(updateCommand);
        // Spec parity: PowerShell-installed PR routes get a PowerShell updateCommand.
        // Mirroring this assertion across the two scripts catches the cross-platform
        // divergence bug where the .ps1 script accidentally emitted the .sh path.
        Assert.Contains("get-aspire-cli-pr.ps1", updateCommand);
        Assert.DoesNotContain("get-aspire-cli-pr.sh", updateCommand);
        // PowerShell update path uses -PRNumber; the .sh-side test asserts -r.
        Assert.Contains("-PRNumber 99999", updateCommand);
    }

    // PR-route install prints the PATH-activation hint via Write-Host. The
    // OS path separator keeps the line valid on both Windows (;) and Unix (:).
    [Fact]
    public async Task WhatIf_PRRoute_PrintsPathHintToStdout()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Add to your shell profile:", result.Output);
        Assert.Contains("$env:PATH", result.Output);
        Assert.Contains(Path.Combine("dogfood", "pr-99999", "bin"), result.Output);
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

    // PR-route sidecar carries route metadata only — never a "channel" key.
    [Fact]
    public async Task WhatIf_PRRouteSidecar_DoesNotContainChannelKey()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "99999", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-99999", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.False(
            doc.RootElement.TryGetProperty("channel", out _),
            $"Sidecar at {sidecarPath} unexpectedly contains a 'channel' key. Content: {sidecarContent}");
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
    // managed route and InstallPathResolver should return Unknown for these installs so
    // downstream commands (e.g. `aspire update --self`) refuse to assume any update channel.
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

        // The mode-A sidecar at <prefix>/.aspire-install.json must NOT be written.
        var prefixSidecar = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.False(File.Exists(prefixSidecar), $"--local-dir install must not write sidecar at {prefixSidecar} (unmanaged route).");

        // Defensive: assert no .aspire-install.json anywhere under the install root —
        // the mode-B sidecar (at <prefix>/bin/.aspire-install.json) and any other
        // location must equally be absent.
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
