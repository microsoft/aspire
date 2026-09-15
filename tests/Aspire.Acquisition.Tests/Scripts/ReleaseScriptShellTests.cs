// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
        Assert.Contains("[DRY RUN] Would run:", result.Output);
        Assert.Contains("aspire setup", result.Output);
        Assert.True(
            result.Output.IndexOf("route sidecar", StringComparison.OrdinalIgnoreCase) <
            result.Output.IndexOf("[DRY RUN] Would run:", StringComparison.Ordinal),
            "Bundle setup should be planned after the install-route sidecar is written.");
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

    [Theory]
    [InlineData("dev", "daily")]
    [InlineData("staging", "staging")]
    [InlineData("release", "stable")]
    [InlineData("", null)]
    public async Task WriteInstallSidecar_PersistsQualityChannel(string quality, string? expectedChannel)
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "install");
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $"write_install_sidecar '{installPath}' '{quality}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(installPath, ".aspire-install.json")));
        Assert.Equal("script", document.RootElement.GetProperty("source").GetString());
        if (expectedChannel is null)
        {
            Assert.False(document.RootElement.TryGetProperty("channel", out _));
        }
        else
        {
            Assert.Equal(expectedChannel, document.RootElement.GetProperty("channel").GetString());
        }
    }

    [Fact]
    public async Task WriteInstallSidecar_UsesPortableMktempTemplate()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "install");

        // BSD mktemp only expands a trailing X run. Reject non-portable templates even on
        // GNU mktemp, which also accepts a suffix after the X run.
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $"mktemp() {{ case \"$1\" in *XXXXXXXX) command mktemp \"$1\" ;; *) return 64 ;; esac; }}; write_install_sidecar '{installPath}' 'staging'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(installPath, ".aspire-install.json")));
    }

    [Fact]
    public async Task ExplicitVersionInstall_WritesSourceOnlySidecar()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "install");

        // Exercise main's --version routing while replacing only the network/archive work.
        // main defaults QUALITY to release even for explicit versions, so this catches that
        // default accidentally leaking into the install sidecar as channel: stable.
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            download_and_install_archive() {
                mkdir -p "$INSTALL_PATH"
                printf '#!/bin/sh\nexit 0\n' > "$INSTALL_PATH/aspire"
                chmod +x "$INSTALL_PATH/aspire"
            }
            setup_cli_bundle() { return 0; }
            main --version '13.2.0-preview.1.25366.3' --install-path '{{installPath}}' --skip-path
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        using var document = JsonDocument.Parse(
            await File.ReadAllBytesAsync(Path.Combine(installPath, ".aspire-install.json")));
        Assert.Collection(
            document.RootElement.EnumerateObject(),
            property =>
            {
                Assert.Equal("source", property.Name);
                Assert.Equal("script", property.Value.GetString());
            });
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
        Assert.Contains("aspire setup", result.Output);
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

    [Fact]
    public async Task DryRunWithExtension_PlansExtensionBeforeBundleSetup()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--install-extension");

        result.EnsureSuccessful();
        var extensionIndex = result.Output.IndexOf("Installing VS Code extension", StringComparison.Ordinal);
        var setupIndex = result.Output.IndexOf("[DRY RUN] Would run:", StringComparison.Ordinal);
        Assert.True(extensionIndex >= 0, "VS Code extension installation should be planned.");
        Assert.True(setupIndex > extensionIndex, "Bundle setup should be planned after VS Code extension installation.");
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("staging")]
    [InlineData("release")]
    public async Task DryRun_DoesNotCreateGlobalAspireConfigJson(string quality)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--dry-run", "--quality", quality);

        result.EnsureSuccessful();

        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(
            File.Exists(globalConfig),
            $"Release script must not write {globalConfig}; channel belongs to the install-scoped sidecar.");

        // The script should not even plan a global-channel write in its dry-run output.
        Assert.DoesNotContain("aspire.config.json", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_DryRun_DoesNotWriteGlobalChannelField()
    {
        // Install scripts must not write channel identity to global application config.
        // The install-scoped sidecar carries it without contaminating unrelated projects.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();
        Assert.DoesNotContain("config set channel", result.Output, StringComparison.OrdinalIgnoreCase);

        var configPath = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(
            File.Exists(configPath),
            $"install.sh must not create global aspire.config.json; found at {configPath}.");
    }

    // Under --dry-run the release-route script must NOT write the script-route
    // sidecar at <prefix>/.aspire-install.json. The describe-but-do-not-do
    // contract requires the script to print a DRYRUN message naming the path it
    // would write, then return without touching the filesystem. A previous
    // implementation wrote the sidecar even under --dry-run, which can leave a
    // stale source=script marker visible to BundleService when the install
    // was never actually performed.
    [Fact]
    public async Task DryRun_DoesNotWriteScriptRouteSidecar_AndAnnouncesPath()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write route sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }

    // The release-route script must not mutate route sidecars under --dry-run,
    // regardless of the configured quality. This guards the dry-run contract on
    // the 'dev' quality path, which historically took a slightly different
    // code branch in the script body.
    [Fact]
    public async Task DryRun_DevQuality_DoesNotWriteScriptRouteSidecar()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write route sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }
}
