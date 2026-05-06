// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the bash release script (get-aspire-cli.sh).
/// These tests validate parameter handling, platform detection, and dry-run behavior
/// without making any modifications to the user environment.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class ReleaseScriptShellTests(ITestOutputHelper testOutput)
{
    private static readonly string s_scriptPath = ScriptPaths.ReleaseShell;
    private readonly ITestOutputHelper _testOutput = testOutput;

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire CLI", result.Output);
    }

    [Fact]
    public async Task ShortHelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-h");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidQuality_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--quality", "invalid-quality");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported quality", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRun_ShowsDownloadAndInstallSteps()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release");

        result.EnsureSuccessful();
        Assert.Contains("download", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("install", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[DRY RUN]", result.Output);
    }

    [Fact]
    public async Task DryRunWithCustomPath_ShowsCustomInstallPath()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom-bin");
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--dry-run",
            "--quality", "release",
            "--install-path", customPath);

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("-v")]
    public async Task VerboseFlag_ShowsDetailedOutput(string flag)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", flag);

        result.EnsureSuccessful();
        // In dry-run mode, the script outputs a download descriptor like:
        // [DRY RUN] Would download aspire-cli-linux-x64.tar.gz from the stable channel
        Assert.Contains("[DRY RUN] Would download", result.Output);
    }

    [Theory]
    [InlineData("--keep-archive")]
    [InlineData("-k")]
    public async Task KeepArchiveFlag_IsAccepted(string flag)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", flag);

        result.EnsureSuccessful();
    }

    [Theory]
    [InlineData("dev", "from the daily channel")]
    [InlineData("staging", "from the staging channel")]
    [InlineData("release", "from the stable channel")]
    public async Task QualityVariants_AreRecognized(string quality, string expectedSource)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", quality, "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("[DRY RUN]", result.Output);
        Assert.Contains(expectedSource, result.Output);
        Assert.DoesNotContain("dotnet", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ga/daily", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rc/daily", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OsOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--os", "linux");

        result.EnsureSuccessful();
        Assert.Contains("linux", result.Output);
    }

    [Fact]
    public async Task ArchOverride_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--arch", "x64");

        result.EnsureSuccessful();
        Assert.Contains("x64", result.Output);
    }

    [Fact]
    public async Task SkipPathFlag_IsRecognized()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("Skipping PATH", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("release")]
    [InlineData("staging")]
    public async Task InstallExtensionWithNonDevQuality_ReturnsError(string quality)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", quality, "--install-extension");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--quality dev", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    // PR2-S11(a): script-route sidecar written under --dry-run at <prefix>/.aspire-install.json
    // with literal {"route":"script"} content. Sidecar is the source of truth that
    // InstallPathResolver consumes to identify Mode A (script route).
    [Fact]
    public async Task DryRun_WritesScriptRouteSidecar_AtPrefixRoot()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.Equal("script", doc.RootElement.GetProperty("route").GetString());
    }

    // PR2-S11(b): the script-route sidecar carries route metadata only — no "channel" key.
    // Channel storage was removed from the global aspire.config.json in PR1-S6; the sidecar
    // must not reintroduce it. This guards against accidental field bleed-through.
    [Fact]
    public async Task DryRun_ScriptRouteSidecar_DoesNotContainChannelKey()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Expected sidecar at {sidecarPath}");

        var sidecarContent = await File.ReadAllTextAsync(sidecarPath);
        using var doc = System.Text.Json.JsonDocument.Parse(sidecarContent);
        Assert.False(
            doc.RootElement.TryGetProperty("channel", out _),
            $"Sidecar at {sidecarPath} unexpectedly contains a 'channel' key. Content: {sidecarContent}");
    }

    // PR2-S11(b) companion: under --dry-run no global aspire.config.json is created.
    // save_global_settings short-circuits in dry-run, but we want a positive filesystem
    // assertion that channel state is not persisted by the install flow.
    [Fact]
    public async Task DryRun_DoesNotCreateGlobalAspireConfigJson()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--skip-path");

        result.EnsureSuccessful();

        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(File.Exists(globalConfig), $"Unexpected global config at {globalConfig}");
    }
}
