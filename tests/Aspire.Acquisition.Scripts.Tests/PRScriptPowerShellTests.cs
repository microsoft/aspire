// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Tests for the PowerShell PR script (get-aspire-cli-pr.ps1).
/// These tests validate parameter handling using -WhatIf (not -DryRun).
/// PowerShell scripts support -WhatIf as the dry-run equivalent.
///
/// Note: These tests use mock gh CLI and are currently skipped on Windows
/// due to PowerShell -WhatIf interaction with mock scripts.
/// </summary>
[RequiresTools(["pwsh"])]
[SkipOnPlatform(TestPlatforms.Windows, "PowerShell -WhatIf with mock gh scripts has issues on Windows")]
public class PRScriptPowerShellTests
{
    private readonly ITestOutputHelper _testOutput;

    public PRScriptPowerShellTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    private async Task<ScriptToolCommand> CreateCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.ps1", env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    [Fact]
    public async Task GetHelp_WithQuestionMark_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.ps1", env, _testOutput);
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
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli-pr.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-?");

        result.EnsureSuccessful();

        // PowerShell help wraps long lines, which can split parameter names across lines
        // (e.g., "PRNumb\n    er]"). Normalize by removing newlines and continuation whitespace.
        var normalized = System.Text.RegularExpressions.Regex.Replace(result.Output, @"\r?\n\s*", "");

        Assert.Contains("PRNumber", normalized);
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
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task WhatIfWithPRNumber_ShowsSteps()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("12345", result.Output);
    }

    [Fact]
    public async Task CustomInstallPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-InstallPath", customPath, "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task RunIdParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-WorkflowRunId", "987654321", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task OSOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-OS", "win", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-Architecture", "x64", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task HiveOnlyFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-HiveOnly", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task SkipExtensionFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-SkipExtension", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task UseInsidersFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-UseInsiders", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task KeepArchiveFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = await CreateCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("-PRNumber", "12345", "-KeepArchive", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task MultipleFlags_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = await CreateCommandWithMockGhAsync(env);

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
    }
}
