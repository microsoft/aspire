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

    [Theory]
    [InlineData("dev", "daily", "17.0.0-preview.1.26509.9", "17.0.0-preview.1.26509.9")]
    [InlineData("staging", "rc/daily", "13.6.1", "13.6.1-preview.1.26506.6")]
    [InlineData("release", "ga/daily", "13.6.1", "13.6.1-preview.1.26506.6")]
    public async Task ResolveOnly_PrintsVersionAndBuildWithoutInstalling(string quality, string alias, string version, string buildVersion)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            secure_curl() {
                [[ "$1" == 'https://aka.ms/dotnet/9/aspire/{{alias}}/aspire-cli-linux-x64.tar.gz' && "$6" == HEAD ]] || return 1
                printf 'HTTP/1.1 301\r\nLocation: https://ci.dot.net/public/aspire/{{buildVersion}}/aspire-cli-linux-x64-{{version}}.tar.gz\r\n\r\nHTTP/2 200\r\n'
            }
            main --quality '{{quality}}' --os linux --arch x64 --resolve-only
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(version, document.RootElement.GetProperty("version").GetString());
        Assert.Equal(buildVersion, document.RootElement.GetProperty("buildVersion").GetString());
        Assert.Empty(Directory.GetFileSystemEntries(env.MockHome));
    }

    [Theory]
    [InlineData("--build-version", "13.6.1-preview.1.26506.6")]
    [InlineData("--resolve-only", "--dry-run")]
    public async Task InvalidResolutionArguments_Fail(string first, string second)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync(first, second);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("not-a-version")]
    public async Task InvalidBuildPin_FailsBeforeDownloading(string buildVersion)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--version", "13.6.1", "--build-version", buildVersion, "--quality", "dev", "--dry-run");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid --build-version", result.Output);
    }

    [Theory]
    [InlineData("dev", "17.0.0-preview.1.26509.9", "17.0.0-preview.1.26509.9", false, "daily", false)]
    [InlineData("staging", "13.6.1", "13.6.1-preview.1.26506.6", false, "staging", false)]
    [InlineData("release", "13.6.1", "13.6.1-preview.1.26506.6", false, "stable", false)]
    [InlineData("dev", "17.0.0-preview.1.26509.9", "17.0.0-preview.1.26509.9", true, "daily", false)]
    [InlineData("dev", "17.0.0-preview.1.26509.9", "17.0.0-preview.1.26509.9", false, "daily", true)]
    [InlineData("release", "13.6.1", "13.6.1-preview.1.26506.6", false, "stable", true)]
    [InlineData("", "13.6.1", "13.6.1-preview.1.26506.6", false, null, true)]
    public async Task QualityInstall_AliasAdvancesAfterResolution_PinsBothDownloadsAndChecksChecksum(
        string quality, string version, string buildVersion, bool corruptChecksum, string? channel, bool reuseResolvedBuild)
    {
        using var env = new TestEnvironment();
        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(Path.Combine(env.TempDirectory, "source"));
        var requestsPath = Path.Combine(env.TempDirectory, "requests");
        var installPath = Path.Combine(env.TempDirectory, "install");
        var qualityArgs = quality.Length > 0 ? $"--quality '{quality}'" : "";
        var pinArgs = reuseResolvedBuild ? $"--version '{version}' --build-version '{buildVersion}'" : "";
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            secure_curl() {
                if [[ "$6" == HEAD ]]; then
                    if [[ "$1" == https://aka.ms/* ]]; then
                        echo 'resolve' >> '{{requestsPath}}'
                        printf 'HTTP/1.1 301\r\nLocation: https://ci.dot.net/public/aspire/{{buildVersion}}/aspire-cli-linux-x64-{{version}}.tar.gz\r\n\r\nHTTP/2 200\r\n'
                    else
                        printf 'HTTP/2 200\r\nContent-Type: application/octet-stream\r\n'
                    fi
                    return 0
                fi
                echo "$1" >> '{{requestsPath}}'
                case "$1" in
                    *.sha512)
                        # The mutable checksum alias now refers to a different build.
                        if [[ "$1" == https://aka.ms/* || '{{corruptChecksum}}' == 'True' ]]; then
                            printf '%0128d' 0 > "$2"
                        else
                            cp '{{archive.ChecksumPath}}' "$2"
                        fi
                        ;;
                    *) cp '{{archive.ArchivePath}}' "$2" ;;
                esac
            }
            main {{qualityArgs}} {{pinArgs}} --os linux --arch x64 --install-path '{{installPath}}' --skip-path --skip-completions
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        var requests = await File.ReadAllLinesAsync(requestsPath);
        Assert.Equal(
            [
                .. reuseResolvedBuild ? Array.Empty<string>() : ["resolve"],
                $"https://ci.dot.net/public/aspire/{buildVersion}/aspire-cli-linux-x64-{version}.tar.gz",
                $"https://ci.dot.net/public-checksums/aspire/{buildVersion}/aspire-cli-linux-x64-{version}.tar.gz.sha512"
            ],
            requests);
        if (corruptChecksum)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Checksum validation failed", result.Output);
            Assert.False(File.Exists(Path.Combine(installPath, "aspire")));
        }
        else
        {
            result.EnsureSuccessful();
            Assert.True(File.Exists(Path.Combine(installPath, "aspire")));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(installPath, ".aspire-install.json")));
            if (channel is null)
            {
                Assert.False(document.RootElement.TryGetProperty("channel", out _));
            }
            else
            {
                Assert.Equal(channel, document.RootElement.GetProperty("channel").GetString());
            }
        }
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);
        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("Usage", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire CLI", result.Output);
        Assert.Contains("--skip-completions", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QualityInstall_WithIndependentExtensionVersion_UsesExtensionQualityAlias(bool reuseResolvedBuild)
    {
        using var env = new TestEnvironment();
        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(Path.Combine(env.TempDirectory, "source"));
        var extensionPath = Path.Combine(env.TempDirectory, "aspire-vscode-1.24.0.vsix.zip");
        await File.WriteAllTextAsync(extensionPath, "extension version 1.24.0");
        var installedExtensionPath = Path.Combine(env.TempDirectory, "installed-extension");
        var requestsPath = Path.Combine(env.TempDirectory, "requests");
        var installPath = Path.Combine(env.TempDirectory, "install");
        var pinArgs = reuseResolvedBuild
            ? "--version '17.0.0-preview.1.26513.5' --build-version '17.0.0-preview.1.26513.5'"
            : "";
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            secure_curl() {
                if [[ "${6:-GET}" == HEAD ]]; then
                    if [[ "$1" == *aspire-vscode* ]]; then
                        [[ "$1" == 'https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip' ]] || return 22
                        printf 'HTTP/1.1 301\r\nLocation: https://ci.dot.net/public/aspire/17.0.0-preview.1.26513.5/aspire-vscode-1.24.0.vsix.zip\r\n\r\nHTTP/2 200\r\nContent-Type: application/octet-stream\r\n'
                    else
                        printf 'HTTP/1.1 301\r\nLocation: https://ci.dot.net/public/aspire/17.0.0-preview.1.26513.5/aspire-cli-linux-x64-17.0.0-preview.1.26513.5.tar.gz\r\n\r\nHTTP/2 200\r\nContent-Type: application/octet-stream\r\n'
                    fi
                    return 0
                fi
                echo "$1" >> '{{requestsPath}}'
                case "$1" in
                    *.sha512) cp '{{archive.ChecksumPath}}' "$2" ;;
                    https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip) cp '{{extensionPath}}' "$2" ;;
                    *aspire-vscode*) return 22 ;;
                    *) cp '{{archive.ArchivePath}}' "$2" ;;
                esac
            }
            test_vscode_cli() { return 0; }
            install_aspire_extension() { cp "$1" '{{installedExtensionPath}}'; }
            main --quality dev {{pinArgs}} --os linux --arch x64 --install-path '{{installPath}}' --install-extension --skip-path --skip-completions
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(installedExtensionPath), "The independently versioned 1.24.0 extension should be downloaded and passed to its installer.");
        Assert.Equal("extension version 1.24.0", await File.ReadAllTextAsync(installedExtensionPath));
        var requests = await File.ReadAllLinesAsync(requestsPath);
        Assert.Equal(
            [
                "https://ci.dot.net/public/aspire/17.0.0-preview.1.26513.5/aspire-cli-linux-x64-17.0.0-preview.1.26513.5.tar.gz",
                "https://ci.dot.net/public-checksums/aspire/17.0.0-preview.1.26513.5/aspire-cli-linux-x64-17.0.0-preview.1.26513.5.tar.gz.sha512",
                "https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip"
            ],
            requests);
    }

    [Fact]
    public async Task SkipCompletions_DryRun_DoesNotGenerateOrRegister()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("--dry-run", "--skip-path", "--skip-completions");

        result.EnsureSuccessful();
        Assert.Contains("Skipping shell completions due to --skip-completions", result.Output);
        Assert.Empty(Directory.GetFileSystemEntries(env.MockHome));
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

    [Theory]
    [InlineData("dev", false)]
    [InlineData("staging", false)]
    [InlineData("release", false)]
    [InlineData("dev", true)]
    [InlineData("staging", true)]
    [InlineData("release", true)]
    public async Task DryRun_ShowsDownloadAndInstallSteps_WithoutRequestsOrInstallWrites(string quality, bool reuseResolvedBuild)
    {
        using var env = new TestEnvironment();
        var requestsPath = Path.Combine(env.TempDirectory, "requests");
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        await File.WriteAllTextAsync(requestsPath, "");
        var pinArgs = reuseResolvedBuild ? "--version '13.6.1' --build-version '13.6.1-preview.1.26506.6'" : "";
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            secure_curl() {
                printf '%s\n' "$*" >> '{{requestsPath}}'
                return 22
            }
            main --dry-run --quality '{{quality}}' {{pinArgs}} --install-path '{{installPath}}'
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Empty(await File.ReadAllLinesAsync(requestsPath));
        Assert.Empty(Directory.GetFileSystemEntries(env.MockHome));
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
