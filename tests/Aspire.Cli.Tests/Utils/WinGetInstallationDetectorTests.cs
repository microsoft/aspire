// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

[SkipOnPlatform(TestPlatforms.AnyUnix, "WinGet detection only applies on Windows.")]
public class WinGetInstallationDetectorTests
{
    private readonly ILogger<InstallationDetector> _logger = NullLoggerFactory.Instance.CreateLogger<InstallationDetector>();
    private static string AspireBinaryName => OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";

    [Fact]
    public void IsWinGetInstall_PathUnderUserWinGetPackages_ReturnsTrue()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var wingetPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(wingetPath);

        Assert.True(result);
    }

    [Fact]
    public void IsWinGetInstall_PathUnderMachineWinGetPackages_ReturnsTrue()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var wingetPath = Path.Combine(programFiles, "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(wingetPath);

        Assert.True(result);
    }

    [Fact]
    public void IsWinGetInstall_PathNotUnderWinGetPackages_ReturnsFalse()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var otherPath = Path.Combine(localAppData, "SomeOtherApp", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(otherPath);

        Assert.False(result);
    }

    [Fact]
    public void IsWinGetInstall_BoundarySafety_SimilarPrefixReturnsFalse()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // "Packages2" should NOT match "Packages" — boundary safety check
        var similarPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages2", "aspire.exe");

        var result = InstallationDetector.IsWinGetInstall(similarPath);

        Assert.False(result);
    }

    [Fact]
    public void GetInstallationInfo_WinGetPath_ReturnsSelfUpdateDisabledWithGenericMessage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var wingetProcessPath = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "Microsoft.Aspire_8wekyb3d8bbwe", "aspire.exe");
        var detector = new InstallationDetector(_logger, wingetProcessPath);

        var info = detector.GetInstallationInfo();

        Assert.False(info.IsDotNetTool);
        Assert.True(info.SelfUpdateDisabled);
        Assert.Equal(InstallationDetector.WinGetUpdateInstructions, info.UpdateInstructions);
    }

    [Fact]
    public void GetInstallationInfo_ConfigFileTakesPriorityOverWinGetPath()
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
                    "updateInstructions": "brew upgrade --cask aspire"
                }
                """);

            var detector = new InstallationDetector(_logger, processPath);

            var info = detector.GetInstallationInfo();

            Assert.True(info.SelfUpdateDisabled);
            Assert.Equal("brew upgrade --cask aspire", info.UpdateInstructions);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}