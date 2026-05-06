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

    // PR2-S11(c)(i): PR-route CLI binary lands at <prefix>/dogfood/pr-<N>/bin so PR installs
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

    // PR2-S11(c)(ii): PR-route sidecar at <prefix>/dogfood/pr-<N>/.aspire-install.json with
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
        Assert.Contains("get-aspire-cli-pr.sh", updateCommand);
        Assert.Contains("-r 99999", updateCommand);
    }

    // PR2-S11(c)(iii): PR-route install prints the PATH-activation hint via Write-Host. The
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

    // PR2-S11(c)(iv): PR-route hive location is unchanged at <prefix>/hives/pr-<N>/packages.
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

    // PR2-S11(d): PR-route sidecar carries route metadata only — never a "channel" key.
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

    // PR2-S11(d) companion: under -WhatIf no global aspire.config.json is created.
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
}
