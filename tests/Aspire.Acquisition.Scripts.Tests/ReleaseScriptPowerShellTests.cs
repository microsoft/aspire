// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Tests for the PowerShell release script (get-aspire-cli.ps1).
/// These tests validate parameter handling using -WhatIf for dry-run.
/// </summary>
[RequiresTools(["pwsh"])]
public class ReleaseScriptPowerShellTests
{
    private readonly ITestOutputHelper _testOutput;

    public ReleaseScriptPowerShellTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    [Fact]
    public async Task HelpFlag_ShowsUsage()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
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
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "invalid-quality");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Quality", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIf_ShowsActions()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("What if", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIfWithCustomPath_IsRecognized()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-InstallPath", customPath, "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    [Fact]
    public async Task QualityParameter_IsRecognized()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Help");

        result.EnsureSuccessful();
        Assert.Contains("Quality", result.Output);
    }

    [Fact]
    public async Task AllMainParameters_ShownInHelp()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
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
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Version", "9.5.0-preview.1.25366.3", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("9.5.0-preview.1.25366.3", result.Output);
    }

    [Fact]
    public async Task MultipleParameters_WorkTogether()
    {
        using var env = new TestEnvironment();
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "-Quality", "dev",
            "-InstallPath", customPath,
            "-SkipPath",
            "-KeepArchive",
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains(customPath, result.Output);
    }

    #region Function-level parity tests (ConvertTo-ChannelName, Get-AspireCliUrl)

    [Theory]
    [InlineData("release", "stable")]
    [InlineData("staging", "staging")]
    [InlineData("dev", "daily")]
    public async Task ConvertToChannelName_KnownQualities_ReturnsMappedChannel(string quality, string expectedChannel)
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
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
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
            "ConvertTo-ChannelName -Quality 'custom-channel'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("custom-channel", result.Output.Trim());
    }

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
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
            $"(Get-AspireCliUrl -Quality '{quality}' -RuntimeIdentifier '{rid}' -Extension '{ext}').ArchiveUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Fact]
    public async Task GetAspireCliUrl_NoVersion_ReturnsChecksumUrl()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
            "(Get-AspireCliUrl -Quality 'release' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ChecksumUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.EndsWith(".sha512", result.Output.Trim());
        Assert.Contains("aka.ms", result.Output.Trim());
    }

    [Fact]
    public async Task GetAspireCliUrl_WithVersion_ReturnsCiDotNetUrl()
    {
        using var env = new TestEnvironment();
        var version = "9.5.0-preview.1.25366.3";
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
            $"(Get-AspireCliUrl -Version '{version}' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ArchiveUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var url = result.Output.Trim();
        Assert.Contains("ci.dot.net", url);
        Assert.Contains(version, url);
        Assert.Contains("linux-x64", url);
    }

    [Fact]
    public async Task GetAspireCliUrl_WithVersion_ChecksumUrl_UsesPublicChecksums()
    {
        using var env = new TestEnvironment();
        var version = "9.5.0-preview.1.25366.3";
        var cmd = new ScriptFunctionCommand(
            "eng/scripts/get-aspire-cli.ps1",
            $"(Get-AspireCliUrl -Version '{version}' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz').ChecksumUrl",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var url = result.Output.Trim();
        Assert.Contains("public-checksums", url);
        Assert.Contains(version, url);
        Assert.EndsWith(".sha512", url);
    }

    [Fact]
    public async Task VersionAndQualityTogether_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-Version", "9.5.0-preview.1.25366.3",
            "-Quality", "dev",
            "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(
            result.Output.Contains("Cannot specify both", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("Version", StringComparison.OrdinalIgnoreCase),
            "Output should indicate version/quality mutual exclusion");
    }

    [Fact]
    public async Task InstallExtensionWithReleaseQuality_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "release", "-InstallExtension", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dev", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallExtensionWithStagingQuality_ReturnsError()
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptToolCommand("eng/scripts/get-aspire-cli.ps1", env, _testOutput);
        var result = await cmd.ExecuteAsync("-Quality", "staging", "-InstallExtension", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("dev", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
