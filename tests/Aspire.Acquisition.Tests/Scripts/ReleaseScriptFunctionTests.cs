// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tier-1 unit tests for individual functions in the release bash script (get-aspire-cli.sh).
/// Tests URL construction, quality mapping, checksum validation, and archive extraction.
/// </summary>
[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class ReleaseScriptFunctionTests(ITestOutputHelper testOutput)
{
    private static readonly string s_releaseScript = ScriptPaths.ReleaseShell;

    private readonly ITestOutputHelper _testOutput = testOutput;

    #region map_quality_to_channel

    [Theory]
    [InlineData("release", "stable")]
    [InlineData("staging", "staging")]
    [InlineData("dev", "daily")]
    public async Task MapQualityToChannel_KnownQualities_ReturnsMappedChannel(string quality, string expectedChannel)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "construct_aspire_cli_url '' 'invalid' 'linux-x64' 'tar.gz'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unsupported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region validate_checksum

    [Fact]
    public async Task ValidateChecksum_MatchingChecksum_Succeeds()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"install_archive '{archive.ArchivePath}' '{destPath}' 'linux'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(destPath, "aspire")),
            "Extracted binary should exist at destination");
    }

    [Fact]
    public async Task InstallArchive_Zip_ExtractsToDestination()
    {
        using var env = new TestEnvironment();

        var archive = await FakeArchiveHelper.CreateFakeArchiveAsync(env.TempDirectory, "win-x64");
        var destPath = Path.Combine(env.TempDirectory, "install-dest-zip");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"install_archive '{archive.ArchivePath}' '{destPath}' 'win'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.True(File.Exists(Path.Combine(destPath, "aspire.exe")),
            "Extracted binary should exist at destination");
    }

    #endregion

    #region detect_os

    [Fact]
    public async Task DetectOs_ReturnsKnownPlatform()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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

    #region detect_architecture

    [Fact]
    public async Task DetectArchitecture_ReturnsKnownArch()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
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

    #region get_cli_architecture_from_architecture

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x64", "x64")]
    [InlineData("arm64", "arm64")]
    [InlineData("AMD64", "x64")]
    [InlineData("X64", "x64")]
    [InlineData("ARM64", "arm64")]
    public async Task GetCliArchitectureFromArchitecture_NormalizesArchNames(string input, string expected)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"get_cli_architecture_from_architecture '{input}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expected, result.Output.Trim());
    }

    [Fact]
    public async Task GetCliArchitectureFromArchitecture_UnsupportedArch_Fails()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "get_cli_architecture_from_architecture 'mips'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not supported", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region construct_aspire_extension_url

    [Theory]
    [InlineData("dev", "https://aka.ms/dotnet/9/aspire/daily/aspire-vscode.vsix.zip")]
    [InlineData("staging", "https://aka.ms/dotnet/9/aspire/rc/daily/aspire-vscode.vsix.zip")]
    [InlineData("release", "https://aka.ms/dotnet/9/aspire/ga/daily/aspire-vscode.vsix.zip")]
    public async Task ConstructAspireExtensionUrl_NoVersion_ReturnsAkaMsUrl(string quality, string expectedUrl)
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"construct_aspire_extension_url '' '{quality}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(expectedUrl, result.Output.Trim());
    }

    [Fact]
    public async Task ConstructAspireExtensionUrl_WithVersion_ReturnsCiDotNetUrl()
    {
        using var env = new TestEnvironment();
        var version = "13.2.0-preview.1.25366.3";
        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"construct_aspire_extension_url '{version}' 'dev'",
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

    #region add_to_shell_profile

    [Fact]
    public async Task AddToShellProfile_Bash_AddsPathToBashrc()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        Directory.CreateDirectory(installPath);

        File.WriteAllText(Path.Combine(env.MockHome, ".bashrc"), "# existing config\n");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"VERBOSE=true; DRY_RUN=false; add_to_shell_profile '{installPath}' '$HOME/.aspire/bin'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("SHELL", "/bin/bash");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var bashrcContent = File.ReadAllText(Path.Combine(env.MockHome, ".bashrc"));
        Assert.Contains("# Added by get-aspire-cli.sh", bashrcContent);
        Assert.Contains("export PATH=\"$HOME/.aspire/bin:$PATH\"", bashrcContent);
    }

    [Fact]
    public async Task AddToShellProfile_Zsh_AddsPathToZshrc()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        Directory.CreateDirectory(installPath);

        File.WriteAllText(Path.Combine(env.MockHome, ".zshrc"), "# existing config\n");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"VERBOSE=true; DRY_RUN=false; add_to_shell_profile '{installPath}' '$HOME/.aspire/bin'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("SHELL", "/bin/zsh");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var zshrcContent = File.ReadAllText(Path.Combine(env.MockHome, ".zshrc"));
        Assert.Contains("# Added by get-aspire-cli.sh", zshrcContent);
        Assert.Contains("export PATH=\"$HOME/.aspire/bin:$PATH\"", zshrcContent);
    }

    [Fact]
    public async Task AddToShellProfile_AlreadyInPath_DoesNotDuplicate()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        Directory.CreateDirectory(installPath);

        var exportLine = "export PATH=\"$HOME/.aspire/bin:$PATH\"";
        File.WriteAllText(
            Path.Combine(env.MockHome, ".bashrc"),
            "# existing config\n\n# Added by get-aspire-cli.sh\n" + exportLine + "\n");

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"VERBOSE=true; DRY_RUN=false; add_to_shell_profile '{installPath}' '$HOME/.aspire/bin'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("SHELL", "/bin/bash");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var bashrcContent = File.ReadAllText(Path.Combine(env.MockHome, ".bashrc"));
        var exportCount = bashrcContent.Split("export PATH=").Length - 1;
        Assert.Equal(1, exportCount);
    }

    [Fact]
    public async Task AddToShellProfile_NoConfigFile_DryRunShowsMessage()
    {
        using var env = new TestEnvironment();
        var installPath = Path.Combine(env.MockHome, ".aspire", "bin");
        Directory.CreateDirectory(installPath);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            $"VERBOSE=true; DRY_RUN=true; add_to_shell_profile '{installPath}' '$HOME/.aspire/bin'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("SHELL", "/bin/bash");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("Would create config file", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region validate_content_type

    [Fact]
    public async Task ValidateContentType_HtmlResponse_Fails()
    {
        using var env = new TestEnvironment();
        var mockBinDir = Path.Combine(env.TempDirectory, "mock-bin");
        Directory.CreateDirectory(mockBinDir);

        var mockCurl = Path.Combine(mockBinDir, "curl");
        await File.WriteAllTextAsync(mockCurl,
            """
            #!/bin/bash
            # Mock curl that returns HTML content-type headers for HEAD requests
            for arg in "$@"; do
                if [ "$arg" = "--head" ] || [ "$arg" = "-I" ]; then
                    echo "HTTP/1.1 200 OK"
                    echo "content-type: text/html; charset=utf-8"
                    echo ""
                    exit 0
                fi
            done
            exit 0
            """);
        FileHelper.MakeExecutable(mockCurl);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "VERBOSE=true; validate_content_type 'http://example.com/file.tar.gz'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}:{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("HTML", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateContentType_BinaryResponse_Succeeds()
    {
        using var env = new TestEnvironment();
        var mockBinDir = Path.Combine(env.TempDirectory, "mock-bin");
        Directory.CreateDirectory(mockBinDir);

        var mockCurl = Path.Combine(mockBinDir, "curl");
        await File.WriteAllTextAsync(mockCurl,
            """
            #!/bin/bash
            # Mock curl that returns binary content-type headers for HEAD requests
            for arg in "$@"; do
                if [ "$arg" = "--head" ] || [ "$arg" = "-I" ]; then
                    echo "HTTP/1.1 200 OK"
                    echo "content-type: application/octet-stream"
                    echo ""
                    exit 0
                fi
            done
            exit 0
            """);
        FileHelper.MakeExecutable(mockCurl);

        using var cmd = new ScriptFunctionCommand(
            s_releaseScript,
            "VERBOSE=true; validate_content_type 'http://example.com/file.tar.gz'",
            env,
            _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockBinDir}:{Environment.GetEnvironmentVariable("PATH")}");

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
    }

    #endregion
}
