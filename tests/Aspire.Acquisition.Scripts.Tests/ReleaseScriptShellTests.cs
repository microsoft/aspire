// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Tests for the bash release script (get-aspire-cli.sh).
/// These tests validate parameter handling, platform detection, and dry-run behavior
/// without making any modifications to the user environment.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class ReleaseScriptShellTests
{
    private readonly ITestOutputHelper _testOutput;

    public ReleaseScriptShellTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire CLI", result.Output);
    }

    [Fact]
    public async Task ShortHelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("-h");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidQuality_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--quality", "invalid-quality");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task DryRun_ShowsDownloadAndInstallSteps()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release");

        result.EnsureSuccessful();
        Assert.Contains("download", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunWithCustomPath_ShowsCustomInstallPath()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom-bin");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--dry-run",
            "--quality", "release",
            "--install-path", customPath);

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task KeepArchiveFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--keep-archive");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ShortKeepArchiveFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "-k");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task VerboseFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--verbose");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ShortVerboseFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "-v");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task QualityDev_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task QualityStaging_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "staging");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task QualityRelease_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ShortQualityFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "-q", "dev");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task OsOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--os", "linux");

        result.EnsureSuccessful();
        Assert.Contains("linux", result.Output);
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--arch", "x64");

        result.EnsureSuccessful();
        Assert.Contains("x64", result.Output);
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task InstallExtensionFlag_RequiresDevQuality()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--install-extension");

        // Extension installation should fail when not using dev quality
        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task InstallExtensionWithDevQuality_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--install-extension");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task UseInsidersFlag_WithExtension_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--install-extension", "--use-insiders");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task VersionParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--version", "9.5.0-preview.1.25366.3");

        result.EnsureSuccessful();
        Assert.Contains("9.5.0-preview.1.25366.3", result.Output);
    }

    [Fact]
    public async Task ShortInstallPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "-i", customPath);

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task MultipleFlags_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "--dry-run",
            "--quality", "dev",
            "--install-path", customPath,
            "--skip-path",
            "--keep-archive",
            "--verbose");

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task DefaultInstallPath_MentionsAspireDirectory()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release");

        result.EnsureSuccessful();
        Assert.Contains(".aspire", result.Output);
    }

    [Fact]
    public async Task UnknownFlag_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.sh", env, _testOutput);
        var result = await cmd.ExecuteAsync("--nonexistent-flag");

        Assert.NotEqual(0, result.ExitCode);
    }
}
