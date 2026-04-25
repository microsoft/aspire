// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Unit tests for individual functions in the PowerShell PR script (get-aspire-cli-pr.ps1).
/// Tests RID computation and version suffix extraction.
/// </summary>
[RequiresTools(["pwsh"])]
public class PRScriptPSFunctionTests(ITestOutputHelper testOutput)
{
    private static readonly string s_prScript = ScriptPaths.PRPowerShell;

    private readonly ITestOutputHelper _testOutput = testOutput;

    #region Get-RuntimeIdentifier

    [Theory]
    [InlineData("linux", "x64", "linux-x64")]
    [InlineData("linux", "arm64", "linux-arm64")]
    [InlineData("osx", "arm64", "osx-arm64")]
    [InlineData("osx", "x64", "osx-x64")]
    [InlineData("win", "x64", "win-x64")]
    [InlineData("win", "arm64", "win-arm64")]
    public async Task GetRuntimeIdentifier_ExplicitOsAndArch_ReturnsExpectedRid(
        string os, string arch, string expectedRid)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-RuntimeIdentifier -_OS '{os}' -_Architecture '{arch}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedRid, result.Output.Trim());
    }

    [Fact]
    public async Task GetRuntimeIdentifier_UnsupportedArch_Fails()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "Get-RuntimeIdentifier -_OS 'linux' -_Architecture 'mips'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not supported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x64", "x64")]
    [InlineData("arm64", "arm64")]
    public async Task GetCliArchitectureFromArchitecture_NormalizesArchNames(string input, string expected)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-CLIArchitectureFromArchitecture -Architecture '{input}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expected, result.Output.Trim());
    }

    #endregion

    #region Get-VersionSuffixFromPackages

    [Fact]
    public async Task GetVersionSuffixFromPackages_ValidNupkg_ReturnsVersionSuffix()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "13.2.0-pr.12345.a1b2c3d4");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-VersionSuffixFromPackages -DownloadDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("pr.12345.a1b2c3d4", result.Output.Trim());
    }

    [Fact]
    public async Task GetVersionSuffixFromPackages_NoNupkgFiles_Fails()
    {
        using var env = new TestEnvironment();

        var emptyDir = Path.Combine(env.TempDirectory, "empty-packages");
        Directory.CreateDirectory(emptyDir);

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-VersionSuffixFromPackages -DownloadDir '{emptyDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task GetVersionSuffixFromPackages_NupkgWithoutPrSuffix_Fails()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "13.2.0-release");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-VersionSuffixFromPackages -DownloadDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task GetVersionSuffixFromPackages_MultipleNupkgs_UsesFirst()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "13.2.0-pr.99999.deadbeef");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Dashboard",
            "13.2.0-pr.99999.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Get-VersionSuffixFromPackages -DownloadDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("pr.99999.deadbeef", result.Output.Trim());
    }

    #endregion

    #region Get-OperatingSystem

    [Fact]
    public async Task GetOperatingSystem_ReturnsKnownPlatform()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "Get-OperatingSystem",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var os = result.Output.Trim();
        Assert.True(
            os is "osx" or "linux" or "linux-musl" or "win",
            $"Expected a recognized OS, got: '{os}'");
    }

    #endregion

    #region Get-MachineArchitecture

    [Fact]
    public async Task GetMachineArchitecture_ReturnsKnownArch()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "Get-MachineArchitecture",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var arch = result.Output.Trim();
        // Get-MachineArchitecture returns raw OS values:
        // Windows: AMD64/amd64/arm64, Unix (modern PS): x64/arm64
        Assert.True(
            arch.Equals("x64", StringComparison.OrdinalIgnoreCase)
            || arch.Equals("amd64", StringComparison.OrdinalIgnoreCase)
            || arch.Equals("arm64", StringComparison.OrdinalIgnoreCase),
            $"Expected x64, amd64, or arm64, got: '{arch}'");
    }

    #endregion

    #region Remove-TempDirectory

    [Fact]
    public async Task RemoveTempDirectory_KeepArchive_RetainsDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"$KeepArchive = $true; Remove-TempDirectory -TempDir '{tempDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(Directory.Exists(tempDir), "Directory should be retained when KeepArchive is true");
    }

    [Fact]
    public async Task RemoveTempDirectory_NoKeepArchive_DeletesDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"$KeepArchive = $false; Remove-TempDirectory -TempDir '{tempDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.False(Directory.Exists(tempDir), "Directory should be deleted when KeepArchive is false");
    }

    [Fact]
    public async Task RemoveTempDirectory_WhatIf_RetainsDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            @"$KeepArchive = $false; $WhatIfPreference = $true; Remove-TempDirectory -TempDir '" + tempDir + "'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(Directory.Exists(tempDir), "Directory should be retained during WhatIf");
    }

    #endregion

    #region Install-AspireCliFromDownload archive selection

    [Fact]
    public async Task InstallAspireCliFromDownload_ZeroArchives_Fails()
    {
        using var env = new TestEnvironment();
        var downloadDir = Path.Combine(env.TempDirectory, "cli-download");
        Directory.CreateDirectory(downloadDir);
        await File.WriteAllTextAsync(Path.Combine(downloadDir, "README.txt"), "no archive here");

        var binDir = Path.Combine(env.TempDirectory, "bin");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Install-AspireCliFromDownload -DownloadDir '{downloadDir}' -CliBinDir '{binDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No CLI archive found", result.Output);
    }

    [Fact]
    public async Task InstallAspireCliFromDownload_MultipleArchives_Fails()
    {
        using var env = new TestEnvironment();
        var downloadDir = Path.Combine(env.TempDirectory, "cli-download");
        Directory.CreateDirectory(downloadDir);
        await File.WriteAllTextAsync(Path.Combine(downloadDir, "aspire-cli-linux-x64.tar.gz"), "fake1");
        await File.WriteAllTextAsync(Path.Combine(downloadDir, "aspire-cli-osx-arm64.tar.gz"), "fake2");

        var binDir = Path.Combine(env.TempDirectory, "bin");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"Install-AspireCliFromDownload -DownloadDir '{downloadDir}' -CliBinDir '{binDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Multiple CLI archives found", result.Output);
    }

    #endregion
}
