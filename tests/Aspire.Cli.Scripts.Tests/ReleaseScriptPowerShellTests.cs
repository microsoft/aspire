// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Tests for the PowerShell release script (get-aspire-cli.ps1).
/// These tests validate parameter handling using -WhatIf for dry-run.
/// </summary>
[RequiresTools(["pwsh"])]
public class ReleaseScriptPowerShellTests
{
    private readonly ITestOutputHelper _testOutput;

    public ReleaseScriptPowerShellTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Help");

        result.EnsureSuccessful();
        Assert.True(
            result.Output.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("PARAMETERS", StringComparison.OrdinalIgnoreCase),
            "Output should contain 'DESCRIPTION' or 'PARAMETERS'");
        Assert.Contains("Aspire CLI", result.Output);
    }

    [Fact]
    public async Task InvalidQuality_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "invalid-quality");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task WhatIf_ShowsActions()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-WhatIf");

        result.EnsureSuccessful();
        Assert.True(result.Output.Length > 0, "Output should not be empty");
    }

    [Fact]
    public async Task WhatIfWithCustomPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-InstallPath", customPath, "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task QualityParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Help");

        result.EnsureSuccessful();
        Assert.Contains("Quality", result.Output);
    }

    [Fact]
    public async Task AllMainParameters_ShownInHelp()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Help");

        result.EnsureSuccessful();

        // PowerShell help wraps long lines, which can split parameter names across lines
        // (e.g., "InstallExten\n    sion"). Normalize by removing newlines and continuation whitespace.
        var normalized = System.Text.RegularExpressions.Regex.Replace(result.Output, @"\r?\n\s*", "");

        Assert.Contains("InstallPath", normalized);
        Assert.Contains("Quality", normalized);
        Assert.Contains("Version", normalized);
        Assert.Contains("OS", normalized);
        Assert.Contains("Architecture", normalized);
        Assert.Contains("InstallExtension", normalized);
        Assert.Contains("UseInsiders", normalized);
        Assert.Contains("SkipPath", normalized);
        Assert.Contains("KeepArchive", normalized);
    }

    [Fact]
    public async Task VersionParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Version", "9.5.0-preview.1.25366.3", "-WhatIf");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task MultipleParameters_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-Quality", "dev",
            "-InstallPath", customPath,
            "-SkipPath",
            "-KeepArchive",
            "-WhatIf");

        result.EnsureSuccessful();
    }
}
