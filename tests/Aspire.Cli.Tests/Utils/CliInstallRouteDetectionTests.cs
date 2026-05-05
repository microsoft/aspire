// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class CliInstallRouteDetectionTests
{
    [Fact]
    public void GetUpdateCommand_ReturnsCommandFromSidecarNextToExecutable()
    {
        using var tempDirectory = new TestTempDirectory();
        var processPath = Path.Combine(tempDirectory.Path, GetAspireExecutableName());
        var sidecarPath = Path.Combine(tempDirectory.Path, ".aspire-install.json");
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(sidecarPath, "{ \"route\": \"winget\", \"updateCommand\": \"winget upgrade Microsoft.Aspire\" }");

        var updateCommand = CliInstallRouteDetection.GetUpdateCommand(processPath, cacheDetectedRoute: false);

        Assert.Equal("winget upgrade Microsoft.Aspire", updateCommand);
    }

    [Fact]
    public void GetUpdateCommand_ReturnsCommandFromSidecarInInstallPrefix()
    {
        using var tempDirectory = new TestTempDirectory();
        var binDirectory = Path.Combine(tempDirectory.Path, "bin");
        Directory.CreateDirectory(binDirectory);
        var processPath = Path.Combine(binDirectory, GetAspireExecutableName());
        var sidecarPath = Path.Combine(tempDirectory.Path, ".aspire-install.json");
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(sidecarPath, "{ \"route\": \"pr\", \"updateCommand\": \"get-aspire-cli-pr.sh -r 12345\" }");

        var updateCommand = CliInstallRouteDetection.GetUpdateCommand(processPath, cacheDetectedRoute: false);

        Assert.Equal("get-aspire-cli-pr.sh -r 12345", updateCommand);
    }

    [Fact]
    public void GetUpdateCommand_ReturnsNullForSidecarWithoutUpdateCommand()
    {
        using var tempDirectory = new TestTempDirectory();
        var processPath = Path.Combine(tempDirectory.Path, GetAspireExecutableName());
        var sidecarPath = Path.Combine(tempDirectory.Path, ".aspire-install.json");
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(sidecarPath, "{ \"route\": \"script\" }");

        var updateCommand = CliInstallRouteDetection.GetUpdateCommand(processPath, cacheDetectedRoute: false);

        Assert.Null(updateCommand);
    }

    [Fact]
    public void TryDetectWinGetInstall_ReturnsTrueForMatchingPortableRegistryEntry()
    {
        const string processPath = @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe\aspire.exe";
        var entries = new[]
        {
            new CliInstallRouteDetection.WinGetPortableInstallEntry(
                "Microsoft.Aspire",
                "Microsoft.Winget.Source_8wekyb3d8bbwe",
                "portable",
                @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe",
                processPath,
                null)
        };

        var detected = CliInstallRouteDetection.TryDetectWinGetInstall(processPath, entries, out var route);

        Assert.True(detected);
        Assert.Equal("winget", route.Route);
        Assert.Equal("winget upgrade Microsoft.Aspire", route.UpdateCommand);
        Assert.Equal(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe", route.SidecarDirectory);
    }

    [Fact]
    public void TryDetectWinGetInstall_ReturnsTrueForMatchingPortableSymlinkRegistryEntry()
    {
        const string targetPath = @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe\aspire.exe";
        const string processPath = @"C:\Users\test\AppData\Local\Microsoft\WinGet\Links\aspire.exe";
        var entries = new[]
        {
            new CliInstallRouteDetection.WinGetPortableInstallEntry(
                "Microsoft.Aspire",
                "Microsoft.Winget.Source_8wekyb3d8bbwe",
                "portable",
                null,
                targetPath,
                processPath)
        };

        var detected = CliInstallRouteDetection.TryDetectWinGetInstall(processPath, entries, out var route);

        Assert.True(detected);
        Assert.Equal("winget upgrade Microsoft.Aspire", route.UpdateCommand);
        Assert.Equal(@"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe", route.SidecarDirectory);
    }

    [Theory]
    [InlineData("Other.Package", "Microsoft.Winget.Source_8wekyb3d8bbwe", "portable")]
    [InlineData("Microsoft.Aspire", "Other.Source", "portable")]
    [InlineData("Microsoft.Aspire", "Microsoft.Winget.Source_8wekyb3d8bbwe", "exe")]
    public void TryDetectWinGetInstall_ReturnsFalseForMismatchedRegistryEntry(string packageIdentifier, string sourceIdentifier, string installerType)
    {
        const string processPath = @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe\aspire.exe";
        var entries = new[]
        {
            new CliInstallRouteDetection.WinGetPortableInstallEntry(
                packageIdentifier,
                sourceIdentifier,
                installerType,
                @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe",
                processPath,
                null)
        };

        var detected = CliInstallRouteDetection.TryDetectWinGetInstall(processPath, entries, out _);

        Assert.False(detected);
    }

    [Fact]
    public void TryDetectWinGetInstall_ReturnsFalseWhenRegistryEntryDoesNotMatchCurrentExecutable()
    {
        const string processPath = @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe\aspire.exe";
        var entries = new[]
        {
            new CliInstallRouteDetection.WinGetPortableInstallEntry(
                "Microsoft.Aspire",
                "Microsoft.Winget.Source_8wekyb3d8bbwe",
                "portable",
                @"C:\Users\test\AppData\Local\Microsoft\WinGet\Packages\Microsoft.Aspire_Microsoft.Winget.Source_8wekyb3d8bbwe",
                @"C:\Other\aspire.exe",
                null)
        };

        var detected = CliInstallRouteDetection.TryDetectWinGetInstall(processPath, entries, out _);

        Assert.False(detected);
    }

    [Fact]
    public void TryWriteSidecar_WritesDetectedRouteSidecar()
    {
        using var tempDirectory = new TestTempDirectory();
        var processPath = Path.Combine(tempDirectory.Path, GetAspireExecutableName());
        var route = new CliInstallRouteDetection.CliInstallRoute("winget", "winget upgrade Microsoft.Aspire", tempDirectory.Path);
        File.WriteAllText(processPath, string.Empty);

        var written = CliInstallRouteDetection.TryWriteSidecar(processPath, route);

        Assert.True(written);
        var sidecarPath = Path.Combine(tempDirectory.Path, ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath));
        using var document = JsonDocument.Parse(File.ReadAllText(sidecarPath));
        Assert.Equal("winget", document.RootElement.GetProperty("route").GetString());
        Assert.Equal("winget upgrade Microsoft.Aspire", document.RootElement.GetProperty("updateCommand").GetString());
    }

    private static string GetAspireExecutableName()
    {
        return OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
    }
}
