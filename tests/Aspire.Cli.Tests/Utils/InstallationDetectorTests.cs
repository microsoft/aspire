// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class InstallationDetectorTests
{
    private readonly ILogger<InstallationDetector> _logger = NullLoggerFactory.Instance.CreateLogger<InstallationDetector>();
    private static string AspireBinaryName => OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
    private static string DotNetBinaryName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetInstallationInfo_NullOrEmptyProcessPath_ReturnsDefault(string? processPath)
    {
        var detector = new InstallationDetector(_logger, processPath: processPath);

        var info = detector.GetInstallationInfo();

        Assert.False(info.IsDotNetTool);
        Assert.False(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.SelfUpdateInstructions, info.UpdateInstructions);
    }

    [Theory]
    [InlineData("/usr/local/share/dotnet/dotnet")]
    [InlineData("some/path/dotnet.exe")]
    public void GetInstallationInfo_DotNetProcessPath_ReturnsDotNetTool(string processPath)
    {
        var detector = new InstallationDetector(_logger, processPath: processPath);

        var info = detector.GetInstallationInfo();

        Assert.True(info.IsDotNetTool);
        Assert.False(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.DotNetToolUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_NoConfigFile_ReturnsDefault()
    {
        // Use a temp directory with no .aspire-update.json
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, ""); // Create a fake binary

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.False(info.SelfUpdateDisabled);
            Assert.Equal(InstallationDetector.SelfUpdateInstructions, info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_ConfigFileWithDisabledTrue_ReturnsSelfUpdateDisabled()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "Please use 'winget upgrade Microsoft.Aspire.Cli' to update."
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.True(info.SelfUpdateDisabled);
            Assert.Equal("Please use 'winget upgrade Microsoft.Aspire.Cli' to update.", info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"selfUpdateDisabled": false}""")]
    [InlineData("""{"updateInstructions": "some instructions"}""")]
    public void GetInstallationInfo_ConfigDoesNotDisableSelfUpdate_ReturnsDefault(string configContent)
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, configContent);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.False(info.SelfUpdateDisabled);
            Assert.Equal(InstallationDetector.SelfUpdateInstructions, info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("not valid json {{{")]
    [InlineData("")]
    [InlineData("null")]
    public void GetInstallationInfo_InvalidConfigContent_FailsClosed(string configContent)
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, configContent);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.True(info.SelfUpdateDisabled);
            Assert.Null(info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_DisabledWithNoInstructions_ReturnsNullInstructions()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.True(info.SelfUpdateDisabled);
            Assert.Null(info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_ResultIsCached()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "use winget"
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info1 = detector.GetInstallationInfo();
            Assert.True(info1.SelfUpdateDisabled);

            // Delete the file - cached result should still be returned
            File.Delete(configPath);

            var info2 = detector.GetInstallationInfo();
            Assert.True(info2.SelfUpdateDisabled);
            Assert.Same(info1, info2);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_JsonWithComments_ParsesSuccessfully()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    // This file was generated by the WinGet package
                    "selfUpdateDisabled": true,
                    "updateInstructions": "Use winget upgrade"
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.True(info.SelfUpdateDisabled);
            Assert.Equal("Use winget upgrade", info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_JsonWithTrailingComma_ParsesSuccessfully()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, AspireBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "Use winget upgrade",
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.True(info.SelfUpdateDisabled);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_DotNetToolTakesPriorityOverConfigFile()
    {
        // Even if .aspire-update.json exists next to dotnet, it should still be detected as a dotnet tool
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, DotNetBinaryName);
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.True(info.IsDotNetTool);
            Assert.False(info.SelfUpdateDisabled);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_FollowsSymlink_ToFindConfigFile()
    {
        // This tests the symlink resolution path critical for Homebrew on macOS,
        // where the binary in /usr/local/bin/aspire is a symlink to the Cellar.
        var targetDir = Directory.CreateTempSubdirectory("aspire-detector-target");
        var linkDir = Directory.CreateTempSubdirectory("aspire-detector-link");
        try
        {
            // Create a fake binary and config in the target directory
            var targetBinaryPath = Path.Combine(targetDir.FullName, AspireBinaryName);
            File.WriteAllText(targetBinaryPath, "");

            var configPath = Path.Combine(targetDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "brew upgrade aspire"
                }
                """);

            // Create a symlink in a different directory pointing to the target binary
            var symlinkPath = Path.Combine(linkDir.FullName, AspireBinaryName);
            try
            {
                File.CreateSymbolicLink(symlinkPath, targetBinaryPath);
            }
            catch (IOException)
            {
                // Symlink creation may fail on some CI environments or due to permissions
                return;
            }

            // Verify the symlink was actually created
            var linkTarget = new FileInfo(symlinkPath).LinkTarget;
            if (linkTarget is null)
            {
                // Not a real symlink (platform doesn't support it), skip
                return;
            }

            // The detector should follow the symlink and find the config next to the real binary
            var detector = new InstallationDetector(_logger, symlinkPath);

            var info = detector.GetInstallationInfo();

            Assert.False(info.IsDotNetTool);
            Assert.True(info.SelfUpdateDisabled);
            Assert.Equal("brew upgrade aspire", info.UpdateInstructions);
        }
        finally
        {
            linkDir.Delete(recursive: true);
            targetDir.Delete(recursive: true);
        }
    }

    [SkipOnPlatform(TestPlatforms.Windows, "This test verifies non-Windows behavior.")]
    [Fact]
    public void IsWinGetInstall_NonWindowsPlatform_ReturnsFalse()
    {
        var result = InstallationDetector.IsWinGetInstall("/some/path/Microsoft/WinGet/Packages/foo/aspire");

        Assert.False(result);
    }

    [SkipOnPlatform(TestPlatforms.Windows, "File permission tests require Unix-style chmod")]
    [Fact]
    public void GetInstallationInfo_UnreadableConfigFile_FailsClosed()
    {
        // When .aspire-update.json exists but is unreadable (e.g., permissions issue),
        // the detector should fail closed — treating it as if self-update is disabled.
        var tempDir = Directory.CreateTempSubdirectory("aspire-install-test");
        try
        {
            var binDir = Path.Combine(tempDir.FullName, "bin");
            Directory.CreateDirectory(binDir);
            var aspireExePath = Path.Combine(binDir, AspireBinaryName);
            File.WriteAllText(aspireExePath, "fake");

            var configFilePath = Path.Combine(binDir, ".aspire-update.json");
            File.WriteAllText(configFilePath, """{ "selfUpdateDisabled": true }""");

            // Make the file unreadable (guarded by SkipOnPlatform attribute)
#pragma warning disable CA1416 // Platform compatibility — guarded by SkipOnPlatform attribute
            File.SetUnixFileMode(configFilePath, UnixFileMode.None);
#pragma warning restore CA1416

            var detector = new InstallationDetector(_logger, processPath: aspireExePath);
            var info = detector.GetInstallationInfo();

            // Should fail closed: treat as self-update disabled
            Assert.True(info.SelfUpdateDisabled);
        }
        finally
        {
            // Restore permissions for cleanup
            var configFilePath = Path.Combine(tempDir.FullName, "bin", ".aspire-update.json");
            if (File.Exists(configFilePath))
            {
#pragma warning disable CA1416 // Platform compatibility — guarded by SkipOnPlatform attribute
                File.SetUnixFileMode(configFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            }

            tempDir.Delete(recursive: true);
        }
    }
}
