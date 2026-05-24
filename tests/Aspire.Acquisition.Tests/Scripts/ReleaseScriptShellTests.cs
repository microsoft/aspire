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

    [Fact]
    public async Task UninstallDryRunWithQuality_ShowsInferredChannelAndSharedInstallSkip()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "aspire", "bin");
        Directory.CreateDirectory(Path.Combine(env.TempDirectory, "aspire", "hives", "staging", "packages"));

        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--uninstall",
            "--quality", "staging",
            "--install-path", customPath,
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains("hive staging", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--remove-shared-install", result.Output);
    }

    [Fact]
    public async Task UninstallWithRemoveSharedInstall_DeletesSharedArtifacts()
    {
        using var env = new TestEnvironment();
        var aspireHome = Path.Combine(env.TempDirectory, "aspire");
        var customPath = Path.Combine(aspireHome, "bin");
        Directory.CreateDirectory(Path.Combine(aspireHome, "hives", "daily", "packages"));
        Directory.CreateDirectory(Path.Combine(aspireHome, "bundle"));
        Directory.CreateDirectory(Path.Combine(aspireHome, "versions", "v1"));
        Directory.CreateDirectory(customPath);
        var binaryPath = Path.Combine(customPath, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, "");

        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--uninstall",
            "--quality", "dev",
            "--install-path", customPath,
            "--yes",
            "--remove-shared-install");

        result.EnsureSuccessful();
        Assert.False(Directory.Exists(Path.Combine(aspireHome, "hives", "daily")));
        Assert.False(File.Exists(binaryPath));
        Assert.False(Directory.Exists(Path.Combine(aspireHome, "bundle")));
        Assert.True(Directory.Exists(Path.Combine(aspireHome, "versions", "v1")));
    }

    [Fact]
    public async Task UninstallWithoutChannelOrAll_FailsWithoutSilentlyTargetingDefaultQualityChannel()
    {
        // Without --channel/--all/--quality, the script must refuse rather than
        // infer "stable" from the default quality and quietly delete it.
        using var env = new TestEnvironment();
        var aspireHome = Path.Combine(env.TempDirectory, "aspire");
        var customPath = Path.Combine(aspireHome, "bin");
        var stableHive = Path.Combine(aspireHome, "hives", "stable");
        Directory.CreateDirectory(Path.Combine(stableHive, "packages"));

        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--uninstall",
            "--install-path", customPath,
            "--yes");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--channel or --all", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(stableHive));
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
            $"Release script must not write {globalConfig}; channel is baked into the CLI binary, not stored globally.");

        // The script should not even plan a global-channel write in its dry-run output.
        Assert.DoesNotContain("aspire.config.json", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_DryRun_DoesNotWriteGlobalChannelField()
    {
        // Install scripts must not write a global aspire.config.json — the channel
        // is baked into the CLI binary at build time and read via
        // IdentityChannelReader. A global channel field would shadow the baked value.
        // Asserts both dry-run stdout shape and absence of the global config file.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();
        Assert.DoesNotContain("config set channel", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"channel\"", result.Output);

        var configPath = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(
            File.Exists(configPath),
            $"install.sh must not create global aspire.config.json; found at {configPath}.");
    }

    // Under --dry-run the release-source script must NOT write the script-source
    // sidecar at <prefix>/.aspire-install.json. The describe-but-do-not-do
    // contract requires the script to print a DRYRUN message naming the path it
    // would write, then return without touching the filesystem. A previous
    // implementation wrote the sidecar even under --dry-run, which can leave a
    // stale source=script marker visible to BundleService when the install
    // was never actually performed.
    [Fact]
    public async Task DryRun_DoesNotWriteScriptSourceSidecar_AndAnnouncesPath()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "release", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write source sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }

    // The release-source script must not mutate source sidecars under --dry-run,
    // regardless of the configured quality. This guards the dry-run contract on
    // the 'dev' quality path, which historically took a slightly different
    // code branch in the script body.
    [Fact]
    public async Task DryRun_DevQuality_DoesNotWriteScriptSourceSidecar()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--dry-run", "--quality", "dev", "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"DRYRUN: would write source sidecar to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under --dry-run, but found one at {sidecarPath}");
    }
}
