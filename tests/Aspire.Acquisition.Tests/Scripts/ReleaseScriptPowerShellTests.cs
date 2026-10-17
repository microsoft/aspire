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
            function Invoke-SecureWebRequest {
                param($Uri, $Method)
                if ($Uri -ne 'https://aka.ms/dotnet/9/aspire/{{alias}}/aspire-cli-linux-x64.tar.gz' -or $Method -ne 'Head') {
                    throw "Unexpected resolution request: $Method $Uri"
                }
                [PSCustomObject]@{
                    BaseResponse = [PSCustomObject]@{
                        RequestMessage = [PSCustomObject]@{ RequestUri = [uri]'https://ci.dot.net/public/aspire/{{buildVersion}}/aspire-cli-linux-x64-{{version}}.tar.gz' }
                    }
                }
            }
            $Quality = '{{quality}}'
            $OS = 'linux'
            $Architecture = 'x64'
            $ResolveOnly = $true
            Start-AspireCliInstallation
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(version, document.RootElement.GetProperty("version").GetString());
        Assert.Equal(buildVersion, document.RootElement.GetProperty("buildVersion").GetString());
        Assert.False(Directory.Exists(Path.Combine(env.MockHome, ".aspire")));
    }

    [Theory]
    [InlineData("-BuildVersion", "13.6.1-preview.1.26506.6")]
    [InlineData("-ResolveOnly", "-WhatIf")]
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

        var result = await cmd.ExecuteAsync("-Version", "13.6.1", "-BuildVersion", buildVersion, "-Quality", "dev", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid -BuildVersion", result.Output);
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
        var pinArgs = reuseResolvedBuild ? $"-Version '{version}' -BuildVersion '{buildVersion}'" : "";
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            function Invoke-SecureWebRequest {
                param($Uri, $Method, $OutFile)
                if ($Method -eq 'Head') {
                    if ($Uri.StartsWith('https://aka.ms/')) {
                        Add-Content '{{requestsPath}}' 'resolve'
                    }
                    return [PSCustomObject]@{
                        Headers = @{ 'Content-Type' = 'application/octet-stream' }
                        BaseResponse = [PSCustomObject]@{
                            RequestMessage = [PSCustomObject]@{ RequestUri = [uri]'https://ci.dot.net/public/aspire/{{buildVersion}}/aspire-cli-linux-x64-{{version}}.tar.gz' }
                        }
                    }
                }
                Add-Content '{{requestsPath}}' $Uri
                if ($Uri.EndsWith('.sha512')) {
                    # The mutable checksum alias now refers to a different build.
                    if ($Uri.StartsWith('https://aka.ms/') -or '{{corruptChecksum}}' -eq 'True') {
                        [System.IO.File]::WriteAllText($OutFile, ('0' * 128))
                    } else {
                        [System.IO.File]::Copy('{{archive.ChecksumPath}}', $OutFile, $true)
                    }
                } else {
                    [System.IO.File]::Copy('{{archive.ArchivePath}}', $OutFile, $true)
                }
            }
            $SkipCompletions = $true
            Install-AspireCli -Quality '{{quality}}' {{pinArgs}} -OS linux -Architecture x64 -InstallPath '{{installPath}}'
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
        var result = await cmd.ExecuteAsync("-Help");

        result.EnsureSuccessful();
        Assert.True(
            result.Output.Contains("DESCRIPTION", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("PARAMETERS", StringComparison.OrdinalIgnoreCase),
            "Output should contain 'DESCRIPTION' or 'PARAMETERS'");
        Assert.Contains("Aspire CLI", result.Output);
        Assert.Contains("-SkipCompletions", result.Output);
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
            ? "-Version '17.0.0-preview.1.26513.5' -BuildVersion '17.0.0-preview.1.26513.5'"
            : "";
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            function Invoke-SecureWebRequest {
                param($Uri, $Method, $OutFile)
                if ($Method -eq 'Head') {
                    $resolvedUri = 'https://ci.dot.net/public/aspire/17.0.0-preview.1.26513.5/aspire-cli-linux-x64-17.0.0-preview.1.26513.5.tar.gz'
                    if ($Uri.Contains('aspire-vscode')) {
                        if ($Uri -ne 'https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip') {
                            throw 'HTTP 404: extension filename does not use the CLI version'
                        }
                        $resolvedUri = 'https://ci.dot.net/public/aspire/17.0.0-preview.1.26513.5/aspire-vscode-1.24.0.vsix.zip'
                    }
                    return [PSCustomObject]@{
                        Headers = @{ 'Content-Type' = 'application/octet-stream' }
                        BaseResponse = [PSCustomObject]@{
                            RequestMessage = [PSCustomObject]@{ RequestUri = [uri]$resolvedUri }
                        }
                    }
                }
                Add-Content '{{requestsPath}}' $Uri
                if ($Uri.EndsWith('.sha512')) {
                    [System.IO.File]::Copy('{{archive.ChecksumPath}}', $OutFile, $true)
                } elseif ($Uri -eq 'https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip') {
                    [System.IO.File]::Copy('{{extensionPath}}', $OutFile, $true)
                } elseif ($Uri.Contains('aspire-vscode')) {
                    throw 'HTTP 404: extension filename does not use the CLI version'
                } else {
                    [System.IO.File]::Copy('{{archive.ArchivePath}}', $OutFile, $true)
                }
            }
            function Test-VSCodeCLIDependency { return $true }
            function Install-AspireExtension {
                param($ExtensionArchive, $UseInsiders)
                [System.IO.File]::Copy($ExtensionArchive, '{{installedExtensionPath}}', $true)
            }
            $InstallExtension = $true
            Install-AspireCli -Quality dev {{pinArgs}} -OS linux -Architecture x64 -InstallPath '{{installPath}}'
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
    public async Task SkipCompletions_WhatIf_DoesNotGenerateOrRegister()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(s_scriptPath, env, _testOutput);

        var result = await cmd.ExecuteAsync("-WhatIf", "-SkipPath", "-SkipCompletions");

        result.EnsureSuccessful();
        Assert.Contains("Skipping shell completions due to -SkipCompletions", result.Output);
        Assert.False(Directory.Exists(Path.Combine(env.MockHome, ".aspire")));
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

    [Theory]
    [InlineData("dev", false)]
    [InlineData("staging", false)]
    [InlineData("release", false)]
    [InlineData("dev", true)]
    [InlineData("staging", true)]
    [InlineData("release", true)]
    public async Task WhatIf_ShowsActions_WithoutRequestsOrInstallWrites(string quality, bool reuseResolvedBuild)
    {
        using var env = new TestEnvironment();
        var requestsPath = Path.Combine(env.TempDirectory, "requests");
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        await File.WriteAllTextAsync(requestsPath, "");
        using var cmd = new ScriptFunctionCommand(
            s_scriptPath,
            $$"""
            function Invoke-SecureWebRequest {
                param($Uri, $Method, $OutFile)
                [System.IO.File]::AppendAllText('{{requestsPath}}', "$Method $Uri`n")
                throw "Unexpected HTTP request: $Method $Uri"
            }
            $Quality = '{{quality}}'
            if (${{reuseResolvedBuild}}) {
                $Version = '13.6.1'
                $BuildVersion = '13.6.1-preview.1.26506.6'
            }
            $InstallPath = '{{installPath}}'
            Start-AspireCliInstallation -WhatIf
            """,
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Empty(await File.ReadAllLinesAsync(requestsPath));
        Assert.False(Directory.Exists(Path.Combine(env.MockHome, ".aspire")));
        Assert.Contains("What if", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Download CLI archive", result.Output);
        Assert.Contains("Install CLI", result.Output);
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
