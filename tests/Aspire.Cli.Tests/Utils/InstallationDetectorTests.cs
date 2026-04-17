// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class InstallationDetectorTests
{
    private readonly ILogger<InstallationDetector> _logger = NullLoggerFactory.Instance.CreateLogger<InstallationDetector>();

    [Fact]
    public void GetInstallationInfo_NoProcessPath_ReturnsDefault()
    {
        var detector = new InstallationDetector(_logger, processPath: null);

        var info = detector.GetInstallationInfo();

        Assert.False(info.IsDotNetTool);
        Assert.False(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.SelfUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_EmptyProcessPath_ReturnsDefault()
    {
        var detector = new InstallationDetector(_logger, processPath: "");

        var info = detector.GetInstallationInfo();

        Assert.False(info.IsDotNetTool);
        Assert.False(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.SelfUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_DotNetProcessPath_ReturnsDotNetTool()
    {
        var detector = new InstallationDetector(_logger, processPath: "/usr/local/share/dotnet/dotnet");

        var info = detector.GetInstallationInfo();

        Assert.True(info.IsDotNetTool);
        Assert.False(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.DotNetToolUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_DotNetExeProcessPath_ReturnsDotNetTool()
    {
        // Use platform-appropriate path separator since Path.GetFileNameWithoutExtension
        // is platform-specific
        var processPath = Path.Combine("some", "path", "dotnet.exe");
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
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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

    [Fact]
    public void GetInstallationInfo_ConfigFileWithDisabledFalse_ReturnsDefault()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": false
                }
                """);

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
    public void GetInstallationInfo_MalformedJson_FailsClosed()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, "not valid json {{{");

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
    public void GetInstallationInfo_EmptyJsonFile_FailsClosed()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, "");

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
    public void GetInstallationInfo_MissingSelfUpdateDisabledField_ReturnsDefault()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "updateInstructions": "some instructions"
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            // selfUpdateDisabled defaults to false, so self-update is not disabled
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
    public void GetInstallationInfo_DisabledWithNoInstructions_ReturnsNullInstructions()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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
            var processPath = Path.Combine(tempDir.FullName, "aspire");
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
            var processPath = Path.Combine(tempDir.FullName, "dotnet");
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
    public void GetInstallationInfo_NullJsonLiteral_FailsClosed()
    {
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, "null");

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
    public void GetInstallationInfo_FollowsSymlink_ToFindConfigFile()
    {
        // This tests the symlink resolution path critical for Homebrew on macOS,
        // where the binary in /usr/local/bin/aspire is a symlink to the Cellar.
        var targetDir = Directory.CreateTempSubdirectory("aspire-detector-target");
        var linkDir = Directory.CreateTempSubdirectory("aspire-detector-link");
        try
        {
            // Create a fake binary and config in the target directory
            var targetBinaryPath = Path.Combine(targetDir.FullName, "aspire");
            File.WriteAllText(targetBinaryPath, "");

            var configPath = Path.Combine(targetDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "brew upgrade aspire"
                }
                """);

            // Create a symlink in a different directory pointing to the target binary
            var symlinkPath = Path.Combine(linkDir.FullName, "aspire");
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

    [Fact]
    public void IsWinGetInstall_NonWindowsPlatform_ReturnsFalse()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "This test verifies non-Windows behavior.");

        var result = InstallationDetector.IsWinGetInstall("/some/path/Microsoft/WinGet/Packages/foo/aspire");

        Assert.False(result);
    }

    [Fact]
    public void IsWinGetInstall_PathUnderUserWinGetPackages_ReturnsTrue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinGet detection only applies on Windows.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(localAppData), "LOCALAPPDATA not available.");

        var wingetPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(wingetPath);

        Assert.True(result);
    }

    [Fact]
    public void IsWinGetInstall_PathUnderMachineWinGetPackages_ReturnsTrue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinGet detection only applies on Windows.");

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.SkipWhen(string.IsNullOrEmpty(programFiles), "PROGRAMFILES not available.");

        var wingetPath = Path.Combine(programFiles, "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(wingetPath);

        Assert.True(result);
    }

    [Fact]
    public void IsWinGetInstall_PathNotUnderWinGetPackages_ReturnsFalse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinGet detection only applies on Windows.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(localAppData), "LOCALAPPDATA not available.");

        // Path that looks similar but is NOT under WinGet\Packages
        var otherPath = Path.Combine(localAppData, "SomeOtherApp", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(otherPath);

        Assert.False(result);
    }

    [Fact]
    public void IsWinGetInstall_BoundarySafety_SimilarPrefixReturnsFalse()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinGet detection only applies on Windows.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(localAppData), "LOCALAPPDATA not available.");

        // "Packages2" should NOT match "Packages" — boundary safety check
        var similarPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages2", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(similarPath);

        Assert.False(result);
    }

    [Fact]
    public void GetInstallationInfo_WinGetPath_ReturnsSelfUpdateDisabledWithGenericMessage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "WinGet detection only applies on Windows.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.SkipWhen(string.IsNullOrEmpty(localAppData), "LOCALAPPDATA not available.");

        // Use a path that looks like a WinGet install (no config file needed)
        var wingetProcessPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");
        var detector = new InstallationDetector(_logger, wingetProcessPath);

        var info = detector.GetInstallationInfo();

        Assert.False(info.IsDotNetTool);
        Assert.True(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.PackageManagerUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_ConfigFileTakesPriorityOverWinGetPath()
    {
        // If .aspire-update.json exists, it should take priority over WinGet path detection
        var tempDir = Directory.CreateTempSubdirectory("aspire-detector-test");
        try
        {
            var processPath = Path.Combine(tempDir.FullName, "aspire");
            File.WriteAllText(processPath, "");

            var configPath = Path.Combine(tempDir.FullName, InstallationDetector.UpdateConfigFileName);
            File.WriteAllText(configPath, """
                {
                    "selfUpdateDisabled": true,
                    "updateInstructions": "brew upgrade --cask aspire"
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            // Config file instructions take priority
            Assert.True(info.SelfUpdateDisabled);
            Assert.Equal("brew upgrade --cask aspire", info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetInstallationInfo_UnreadableConfigFile_FailsClosed()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            "File permission tests require Unix-style chmod");

        // When .aspire-update.json exists but is unreadable (e.g., permissions issue),
        // the detector should fail closed — treating it as if self-update is disabled.
        var tempDir = Directory.CreateTempSubdirectory("aspire-install-test");
        try
        {
            var binDir = Path.Combine(tempDir.FullName, "bin");
            Directory.CreateDirectory(binDir);
            var aspireExePath = Path.Combine(binDir, "aspire");
            File.WriteAllText(aspireExePath, "fake");

            var configFilePath = Path.Combine(binDir, ".aspire-update.json");
            File.WriteAllText(configFilePath, """{ "selfUpdateDisabled": true }""");

            // Make the file unreadable (guarded by Assert.SkipUnless above)
#pragma warning disable CA1416 // Platform compatibility — guarded by runtime check above
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
#pragma warning disable CA1416 // Platform compatibility — guarded by runtime check above
                File.SetUnixFileMode(configFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416
            }

            tempDir.Delete(recursive: true);
        }
    }
}
