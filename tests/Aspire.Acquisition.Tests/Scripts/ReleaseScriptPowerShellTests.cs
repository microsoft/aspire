// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the PowerShell release script (get-aspire-cli.ps1).
/// These tests validate parameter handling using -WhatIf for dry-run.
/// </summary>
[RequiresTools(["pwsh"])]
public class ReleaseScriptPowerShellTests(ITestOutputHelper testOutput)
{
    private static readonly string s_scriptPath = ScriptPaths.ReleasePowerShell;
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
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
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "invalid-quality");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Quality", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIf_ShowsActions()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("What if", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIfWithCustomPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-InstallPath", customPath, "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task AllMainParameters_ShownInHelp()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
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
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Version", "13.2.0-preview.1.25366.3", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("13.2.0-preview.1.25366.3", result.Output);
    }

    [Fact]
    public async Task MultipleParameters_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-Quality", "dev",
            "-InstallPath", customPath,
            "-SkipPath",
            "-KeepArchive",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Skipping PATH", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OsOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-OS", "linux", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("linux", result.Output);
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-Architecture", "x64", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("x64", result.Output);
    }

    [Theory]
    [InlineData("dev", "from the daily channel")]
    [InlineData("staging", "from the staging channel")]
    [InlineData("release", "from the stable channel")]
    public async Task QualityVariants_AreRecognized(string quality, string expectedSource)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", quality, "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("What if", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedSource, result.Output);
        Assert.DoesNotContain("dotnet", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ga/daily", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rc/daily", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DefaultInstallPath_MentionsAspireDirectory()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(".aspire", result.Output);
    }

    [Fact]
    public async Task DryRunWithVersion_ShowsVersionInOutput()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Version", "13.2.0-preview.1.25366.3", "-Verbose", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("13.2.0-preview.1.25366.3", result.Output);
    }

    [Fact]
    public async Task VersionAndQualityTogether_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-Version", "13.2.0-preview.1.25366.3",
            "-Quality", "dev",
            "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cannot specify both", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("staging")]
    public async Task InstallExtensionWithNonDevQuality_ReturnsError(string quality)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", quality, "-InstallExtension", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dev", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // PR2-S11(a): script-route sidecar written under -WhatIf at <prefix>/.aspire-install.json.
    // The PowerShell script bypasses ShouldProcess for the sidecar write (uses raw .NET I/O),
    // so the file is observable even when WhatIfPreference is in effect.
    [Fact]
    public async Task WhatIf_WritesScriptRouteSidecar_AtPrefixRoot()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.Equal("script", doc.RootElement.GetProperty("route").GetString());
    }

    // PR2-S11(b): the script-route sidecar is route metadata only — no "channel" key.
    [Fact]
    public async Task WhatIf_ScriptRouteSidecar_DoesNotContainChannelKey()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "dev", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.False(
            doc.RootElement.TryGetProperty("channel", out _),
            $"Sidecar at {sidecarPath} unexpectedly contains a 'channel' key. Content: {sidecarContent}");
    }

    // PR2-S11(b) companion: under -WhatIf no global aspire.config.json is created.
    [Fact]
    public async Task WhatIf_DoesNotCreateGlobalAspireConfigJson()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "dev", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(File.Exists(globalConfig), $"Unexpected global config at {globalConfig}");
    }
}
