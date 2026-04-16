// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tier-1 unit tests for individual functions in the release PowerShell script (get-aspire-cli.ps1).
/// Tests URL construction, quality mapping, checksum validation, and archive extraction.
/// </summary>
[RequiresTools(["pwsh"])]
public class ReleaseScriptPSFunctionTests(ITestOutputHelper testOutput)
{
    private static readonly string s_releaseScript = ScriptPaths.ReleasePowerShell;

    private readonly ITestOutputHelper _testOutput = testOutput;

    #region ConvertTo-ChannelName

    [Theory]
    [InlineData("release", "stable")]
    [InlineData("staging", "staging")]
    [InlineData("dev", "daily")]
    public async Task ConvertToChannelName_KnownQualities_ReturnsMappedChannel(string quality, string expectedChannel)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"ConvertTo-ChannelName -Quality '{quality}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedChannel, result.Output.Trim());
    }

    [Fact]
    public async Task ConvertToChannelName_UnknownQuality_ReturnsAsIs()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "ConvertTo-ChannelName -Quality 'custom-channel'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("custom-channel", result.Output.Trim());
    }

    #endregion

    #region Get-AspireCliUrl

    [Theory]
    [InlineData("release", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("dev", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("staging", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/rc/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("release", "osx-arm64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-osx-arm64.tar.gz")]
    [InlineData("release", "win-x64", "zip", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-win-x64.zip")]
    public async Task GetAspireCliUrl_NoVersion_ReturnsAkaMsUrl(
        string quality, string rid, string ext, string expectedUrl)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"(Get-AspireCliUrl -Quality '{quality}' -RuntimeIdentifier '{rid}' -Extension '{ext}').ArchiveUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Theory]
    [InlineData("release", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz.sha512")]
    [InlineData("dev", "osx-arm64", "tar.gz", "https://aka.ms/dotnet/9/aspire/daily/aspire-cli-osx-arm64.tar.gz.sha512")]
    public async Task GetAspireCliUrl_NoVersionChecksum_ReturnsChecksumUrl(
        string quality, string rid, string ext, string expectedUrl)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"(Get-AspireCliUrl -Quality '{quality}' -RuntimeIdentifier '{rid}' -Extension '{ext}').ChecksumUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Fact]
    public async Task GetAspireCliUrl_WithVersion_ReturnsCiDotNetUrl()
    {
        using var env = new TestEnvironment();
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"(Get-AspireCliUrl -Version '{version}' -Quality 'release' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ArchiveUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var url = result.Output.Trim();
        Assert.Contains("ci.dot.net/public/aspire", url);
        Assert.Contains(version, url);
        Assert.Contains("linux-x64", url);
    }

    [Fact]
    public async Task GetAspireCliUrl_WithVersionChecksum_ReturnsChecksumUrl()
    {
        using var env = new TestEnvironment();
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"(Get-AspireCliUrl -Version '{version}' -Quality 'release' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ChecksumUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var url = result.Output.Trim();
        Assert.Contains("ci.dot.net/public-checksums/aspire", url);
        Assert.Contains(version, url);
        Assert.EndsWith(".sha512", url);
    }

    [Fact]
    public async Task GetAspireCliUrl_UnsupportedQualityNoVersion_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "(Get-AspireCliUrl -Quality 'invalid' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ArchiveUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Test-FileChecksum

    [Fact]
    public async Task TestFileChecksum_MatchingChecksum_Succeeds()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Test-FileChecksum -ArchiveFile '{archive.ArchivePath}' -ChecksumFile '{archive.ChecksumPath}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task TestFileChecksum_MismatchedChecksum_Fails()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveWithBadChecksumAsync(env.TempDirectory);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Test-FileChecksum -ArchiveFile '{archive.ArchivePath}' -ChecksumFile '{archive.ChecksumPath}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Checksum validation failed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Expand-AspireCliArchive

    [Fact]
    public async Task ExpandAspireCliArchive_TarGz_ExtractsToDestination()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory, "linux-x64");
        var destPath = Path.Combine(env.TempDirectory, "install-dest");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Expand-AspireCliArchive -ArchiveFile '{archive.ArchivePath}' -DestinationPath '{destPath}' -OS 'linux'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(destPath, "aspire")),
            "Extracted binary should exist at destination");
    }

    [Fact]
    public async Task ExpandAspireCliArchive_Zip_ExtractsToDestination()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory, "win-x64");
        var destPath = Path.Combine(env.TempDirectory, "install-dest-zip");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Expand-AspireCliArchive -ArchiveFile '{archive.ArchivePath}' -DestinationPath '{destPath}' -OS 'win'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(destPath, "aspire.exe")),
            "Extracted binary should exist at destination");
    }

    #endregion

    #region Get-OperatingSystem

    [Fact]
    public async Task GetOperatingSystem_ReturnsKnownPlatform()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
            s_releaseScript,
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

    #region Get-CLIArchitectureFromArchitecture

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x64", "x64")]
    [InlineData("arm64", "arm64")]
    [InlineData("AMD64", "x64")]
    [InlineData("X64", "x64")]
    [InlineData("ARM64", "arm64")]
    public async Task GetCLIArchitectureFromArchitecture_NormalizesArchNames(string input, string expected)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Get-CLIArchitectureFromArchitecture -Architecture '{input}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expected, result.Output.Trim());
    }

    [Fact]
    public async Task GetCLIArchitectureFromArchitecture_UnsupportedArch_Fails()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "Get-CLIArchitectureFromArchitecture -Architecture 'mips'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not supported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Get-AspireExtensionUrl

    [Theory]
    [InlineData("dev", "https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip")]
    [InlineData("staging", "https://aka.ms/dotnet/9/aspire/rc/daily/aspire-vscode.vsix.zip")]
    [InlineData("release", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-vscode.vsix.zip")]
    public async Task GetAspireExtensionUrl_NoVersion_ReturnsAkaMsUrl(string quality, string expectedUrl)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Get-AspireExtensionUrl -Quality '{quality}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Fact]
    public async Task GetAspireExtensionUrl_WithVersion_ReturnsCiDotNetUrl()
    {
        using var env = new TestEnvironment();
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"Get-AspireExtensionUrl -Version '{version}' -Quality 'dev'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var url = result.Output.Trim();
        Assert.Contains("ci.dot.net/public/aspire", url);
        Assert.Contains(version, url);
        Assert.Contains("vsix.zip", url);
    }

    #endregion
}
