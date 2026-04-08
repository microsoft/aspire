// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Unit tests for individual functions in the bash PR script (get-aspire-cli-pr.sh).
/// Tests RID computation and version suffix extraction.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class PRScriptFunctionTests
{
    private const string PRScript = "eng/scripts/get-aspire-cli-pr.sh";

    private readonly ITestOutputHelper _testOutput;

    public PRScriptFunctionTests(ITestOutputHelper testOutput)
    {
        _testOutput = testOutput;
    }

    #region get_runtime_identifier

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
        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"get_runtime_identifier '{os}' '{arch}'",
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
        var cmd = new ScriptFunctionCommand(
            PRScript,
            "get_runtime_identifier 'linux' 'mips'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x64", "x64")]
    [InlineData("arm64", "arm64")]
    public async Task GetCliArchitectureFromArchitecture_NormalizesArchNames(string input, string expected)
    {
        using var env = new TestEnvironment();
        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"get_cli_architecture_from_architecture '{input}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expected, result.Output.Trim());
    }

    #endregion

    #region extract_version_suffix_from_packages

    [Fact]
    public async Task ExtractVersionSuffix_ValidNupkg_ReturnsVersionSuffix()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "9.5.0-pr.12345.a1b2c3d4");

        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"extract_version_suffix_from_packages '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("pr.12345.a1b2c3d4", result.Output.Trim());
    }

    [Fact]
    public async Task ExtractVersionSuffix_NoNupkgFiles_Fails()
    {
        using var env = new TestEnvironment();

        var emptyDir = Path.Combine(env.TempDirectory, "empty-packages");
        Directory.CreateDirectory(emptyDir);

        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"extract_version_suffix_from_packages '{emptyDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ExtractVersionSuffix_NupkgWithoutPrSuffix_Fails()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "9.5.0-release");

        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"extract_version_suffix_from_packages '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task ExtractVersionSuffix_MultipleNupkgs_UsesFirst()
    {
        using var env = new TestEnvironment();

        var pkgDir = Path.Combine(env.TempDirectory, "packages");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Hosting",
            "9.5.0-pr.99999.deadbeef");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Dashboard",
            "9.5.0-pr.99999.deadbeef");

        var cmd = new ScriptFunctionCommand(
            PRScript,
            $"extract_version_suffix_from_packages '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("pr.99999.deadbeef", result.Output.Trim());
    }

    #endregion
}
