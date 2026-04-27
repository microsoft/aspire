// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Unit tests for individual functions in the bash PR script (get-aspire-cli-pr.sh).
/// Tests RID computation and version suffix extraction.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class PRScriptFunctionTests(ITestOutputHelper testOutput)
{
    private static readonly string s_prScript = ScriptPaths.PRShell;

    private readonly ITestOutputHelper _testOutput = testOutput;

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
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
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
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "get_runtime_identifier 'linux' 'mips'",
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
            "13.2.0-pr.12345.a1b2c3d4");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
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

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
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
            "13.2.0-release");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
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
            "13.2.0-pr.99999.deadbeef");
        await FakeArchiveHelper.CreateFakeNupkgAsync(
            pkgDir,
            "Aspire.Dashboard",
            "13.2.0-pr.99999.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"extract_version_suffix_from_packages '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("pr.99999.deadbeef", result.Output.Trim());
    }

    #endregion

    #region detect_os (PR script copy)

    [Fact]
    public async Task DetectOs_ReturnsKnownPlatform()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "detect_os",
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

    #region detect_architecture (PR script copy)

    [Fact]
    public async Task DetectArchitecture_ReturnsKnownArch()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            "detect_architecture",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var arch = result.Output.Trim();
        Assert.True(
            arch is "x64" or "arm64",
            $"Expected x64 or arm64, got: '{arch}'");
    }

    #endregion

    #region remove_temp_dir

    [Fact]
    public async Task RemoveTempDir_KeepArchive_RetainsDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"KEEP_ARCHIVE=true; DRY_RUN=false; VERBOSE=true; remove_temp_dir '{tempDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(Directory.Exists(tempDir), "Directory should be retained when KEEP_ARCHIVE=true");
    }

    [Fact]
    public async Task RemoveTempDir_NoKeepArchive_DeletesDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"KEEP_ARCHIVE=false; DRY_RUN=false; VERBOSE=true; remove_temp_dir '{tempDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.False(Directory.Exists(tempDir), "Directory should be deleted when KEEP_ARCHIVE=false");
    }

    [Fact]
    public async Task RemoveTempDir_DryRun_RetainsDirectory()
    {
        using var env = new TestEnvironment();
        var tempDir = Path.Combine(env.TempDirectory, "download-temp");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "artifact.tar.gz"), "fake");

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"KEEP_ARCHIVE=false; DRY_RUN=true; VERBOSE=true; remove_temp_dir '{tempDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(Directory.Exists(tempDir), "Directory should be retained during dry run");
    }

    #endregion

    #region download_aspire_cli archive selection

    [Fact]
    public async Task DownloadAspireCli_ZeroArchives_Fails()
    {
        using var env = new TestEnvironment();
        var mockBinDir = await env.CreateMockGhScriptAsync(_testOutput);
        var tempDir = Path.Combine(env.TempDirectory, "work");
        Directory.CreateDirectory(tempDir);

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"VERBOSE=true; DRY_RUN=false; download_aspire_cli '987654321' 'linux-x64' '{tempDir}'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}:{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("ASPIRE_REPO", "dotnet/aspire");
        // Mock gh creates a non-matching file so the archive search finds nothing
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", "README.txt");

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No CLI archive found", result.Output);
    }

    [Fact]
    public async Task DownloadAspireCli_MultipleArchives_Fails()
    {
        using var env = new TestEnvironment();
        var mockBinDir = await env.CreateMockGhScriptAsync(_testOutput);
        var tempDir = Path.Combine(env.TempDirectory, "work");
        Directory.CreateDirectory(tempDir);

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"VERBOSE=true; DRY_RUN=false; download_aspire_cli '987654321' 'linux-x64' '{tempDir}'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}:{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("ASPIRE_REPO", "dotnet/aspire");
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", "aspire-cli-linux-x64.tar.gz\naspire-cli-osx-arm64.tar.gz");

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Multiple CLI archives found", result.Output);
    }

    [Fact]
    public async Task DownloadAspireCli_SingleArchive_ReturnsPath()
    {
        using var env = new TestEnvironment();
        var mockBinDir = await env.CreateMockGhScriptAsync(_testOutput);
        var tempDir = Path.Combine(env.TempDirectory, "work");
        Directory.CreateDirectory(tempDir);

        using var cmd = new ScriptFunctionCommand(
            s_prScript,
            $"VERBOSE=true; DRY_RUN=false; download_aspire_cli '987654321' 'linux-x64' '{tempDir}'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}:{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("ASPIRE_REPO", "dotnet/aspire");
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", "aspire-cli-linux-x64.tar.gz");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("aspire-cli-linux-x64.tar.gz", result.Output);
    }

    #endregion
}
