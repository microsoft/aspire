// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Tier-1 unit tests for individual functions in the release bash script (get-aspire-cli.sh).
/// Tests URL construction, quality mapping, checksum validation, and archive extraction.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class ReleaseScriptFunctionTests
{
    private const string ReleaseScript = "eng/scripts/get-aspire-cli.sh";

    private readonly ITestOutputHelper _testOutput;

    public ReleaseScriptFunctionTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    #region map_quality_to_channel

    [Theory]
    [InlineData("release", "stable")]
    [InlineData("staging", "staging")]
    [InlineData("dev", "daily")]
    public async Task MapQualityToChannel_KnownQualities_ReturnsMappedChannel(string quality, string expectedChannel)
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"map_quality_to_channel '{quality}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedChannel, result.Output.Trim());
    }

    [Fact]
    public async Task MapQualityToChannel_UnknownQuality_ReturnsAsIs()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            "map_quality_to_channel 'custom-channel'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("custom-channel", result.Output.Trim());
    }

    #endregion

    #region construct_aspire_cli_url

    [Theory]
    [InlineData("release", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("dev", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("staging", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/rc/daily/aspire-cli-linux-x64.tar.gz")]
    [InlineData("release", "osx-arm64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-osx-arm64.tar.gz")]
    [InlineData("release", "win-x64", "zip", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-win-x64.zip")]
    public async Task ConstructAspireCliUrl_NoVersion_ReturnsAkaMsUrl(
        string quality, string rid, string ext, string expectedUrl)
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"construct_aspire_cli_url '' '{quality}' '{rid}' '{ext}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Theory]
    [InlineData("release", "linux-x64", "tar.gz", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz.sha512")]
    [InlineData("dev", "osx-arm64", "tar.gz", "https://aka.ms/dotnet/9/aspire/daily/aspire-cli-osx-arm64.tar.gz.sha512")]
    public async Task ConstructAspireCliUrl_NoVersionWithChecksum_ReturnsChecksumUrl(
        string quality, string rid, string ext, string expectedUrl)
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"construct_aspire_cli_url '' '{quality}' '{rid}' '{ext}' 'true'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Fact]
    public async Task ConstructAspireCliUrl_WithVersion_ReturnsCiDotNetUrl()
    {
        using var env = new TestEnvironment();
        var version = "9.5.0-preview.1.25366.3";
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"construct_aspire_cli_url '{version}' 'release' 'linux-x64' 'tar.gz'",
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
    public async Task ConstructAspireCliUrl_WithVersionAndChecksum_ReturnsChecksumUrl()
    {
        using var env = new TestEnvironment();
        var version = "9.5.0-preview.1.25366.3";
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"construct_aspire_cli_url '{version}' 'release' 'linux-x64' 'tar.gz' 'true'",
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
    public async Task ConstructAspireCliUrl_UnsupportedQualityNoVersion_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            "construct_aspire_cli_url '' 'invalid' 'linux-x64' 'tar.gz'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    #endregion

    #region validate_checksum

    [Fact]
    public async Task ValidateChecksum_MatchingChecksum_Succeeds()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory);

        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"validate_checksum '{archive.ArchivePath}' '{archive.ChecksumPath}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
    }

    [Fact]
    public async Task ValidateChecksum_MismatchedChecksum_Fails()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveWithBadChecksumAsync(env.TempDirectory);

        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"validate_checksum '{archive.ArchivePath}' '{archive.ChecksumPath}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Checksum validation failed", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region install_archive

    [Fact]
    public async Task InstallArchive_TarGz_ExtractsToDestination()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory, "linux-x64");
        var destPath = Path.Combine(env.TempDirectory, "install-dest");

        var cmd = new ScriptFunctionCommand(
            ReleaseScript,
            $"install_archive '{archive.ArchivePath}' '{destPath}' 'linux'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(destPath, "aspire")),
            "Extracted binary should exist at destination");
    }

    #endregion
}
