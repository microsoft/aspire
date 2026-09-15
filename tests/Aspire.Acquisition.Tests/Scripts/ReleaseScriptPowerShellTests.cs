// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
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
        Assert.Contains("Set up Aspire CLI bundle", result.Output);
        Assert.True(
            result.Output.IndexOf("Write route sidecar", StringComparison.Ordinal) <
            result.Output.IndexOf("Set up Aspire CLI bundle", StringComparison.Ordinal),
            "Bundle setup should be planned after the install-route sidecar is written.");
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
        Assert.Contains("Set up Aspire CLI bundle", result.Output);
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
            $"Write-InstallSidecar -InstallPath '{installPath}' -Quality '{quality}'",
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteInstallSidecar_WithoutFileMoveOverwrite_CreatesOrReplacesSidecar(bool sidecarExists)
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "install");
        Directory.CreateDirectory(installPath);
        var sidecarPath = Path.Combine(installPath, ".aspire-install.json");
        if (sidecarExists)
        {
            await File.WriteAllTextAsync(sidecarPath, """{"source":"old"}""");
        }

        // CI runs these tests with PowerShell 7+. Force the compatibility branch used by
        // PowerShell 6 and Windows PowerShell 4/5.1 without requiring those hosts in CI.
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $"$Script:SupportsFileMoveOverwrite = $false; Write-InstallSidecar -InstallPath '{installPath}' -Quality 'staging'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecarPath));
        Assert.Equal("script", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("staging", document.RootElement.GetProperty("channel").GetString());
        Assert.Collection(
            Directory.GetFiles(installPath),
            path => Assert.Equal(sidecarPath, path));
    }

    [Fact]
    public async Task ExplicitVersionInstall_WritesSourceOnlySidecar()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.TempDirectory, "install");
        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory);

        // Pass a non-empty quality deliberately so the test fails if Install-AspireCli stops
        // giving explicit versions precedence when it chooses the sidecar identity.
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            $archiveSource = '{{archive.ArchivePath}}'
            $checksumSource = '{{archive.ChecksumPath}}'
            function Invoke-FileDownload {
                param([string]$Uri, [int]$TimeoutSec, [string]$OutputPath)
                $source = if ($OutputPath.EndsWith('.sha512')) { $checksumSource } else { $archiveSource }
                [System.IO.File]::Copy($source, $OutputPath, $true)
            }
            Install-AspireCli `
                -InstallPath '{{installPath}}' `
                -Version '13.2.0-preview.1.25366.3' `
                -Quality 'release' `
                -OS 'linux' `
                -Architecture 'x64'
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

    [Fact]
    public async Task WhatIfWithExtension_PlansExtensionBeforeBundleSetup()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("-Quality", "dev", "-InstallExtension", "-WhatIf");

        result.EnsureSuccessful();
        var extensionIndex = result.Output.IndexOf("Installing VS Code extension", StringComparison.Ordinal);
        var setupIndex = result.Output.IndexOf("Set up Aspire CLI bundle", StringComparison.Ordinal);
        Assert.True(extensionIndex >= 0, "VS Code extension installation should be planned.");
        Assert.True(setupIndex > extensionIndex, "Bundle setup should be planned after VS Code extension installation.");
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("staging")]
    [InlineData("release")]
    public async Task WhatIf_DoesNotCreateGlobalAspireConfigJson(string quality)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("-Quality", quality, "-WhatIf");

        result.EnsureSuccessful();

        var globalConfig = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(
            File.Exists(globalConfig),
            $"Release script must not write {globalConfig}; channel belongs to the install-scoped sidecar.");

        // The script should not even plan a global-channel write in its what-if output.
        Assert.DoesNotContain("aspire.config.json", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_DryRun_DoesNotWriteGlobalChannelField()
    {
        // Install scripts must not write channel identity to global application config.
        // The install-scoped sidecar carries it without contaminating unrelated projects.
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("-Quality", "release", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.DoesNotContain("config set channel", result.Output, StringComparison.OrdinalIgnoreCase);

        var configPath = Path.Combine(env.MockHome, ".aspire", "aspire.config.json");
        Assert.False(
            File.Exists(configPath),
            $"install.ps1 must not create global aspire.config.json; found at {configPath}.");
    }

    // Under -WhatIf the release-route script must NOT write the script-route
    // sidecar at <prefix>/.aspire-install.json. The describe-but-do-not-do
    // contract requires the script to print a "What if:" message naming the path
    // it would write, then return without touching the filesystem. A previous
    // implementation bypassed -WhatIf for the sidecar write (raw .NET I/O), which
    // can leave a stale source=script marker visible to BundleService when
    // the install was never actually performed.
    [Fact]
    public async Task WhatIf_DoesNotWriteScriptRouteSidecar_AndAnnouncesPath()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"What if: Route sidecar would be written to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under -WhatIf, but found one at {sidecarPath}");
    }

    // The release-route script must not mutate route sidecars under -WhatIf,
    // regardless of the configured quality.
    [Fact]
    public async Task WhatIf_DevQuality_DoesNotWriteScriptRouteSidecar()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "dev", "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "bin", ".aspire-install.json");
        Assert.Contains($"What if: Route sidecar would be written to: {sidecarPath}", result.Output);
        Assert.False(
            File.Exists(sidecarPath),
            $"Expected no sidecar to be written under -WhatIf, but found one at {sidecarPath}");
    }
}
