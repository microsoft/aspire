// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class BrewInstallDetectionTests
{
    [Fact]
    public void GetBrewUpdateCommand_ReturnsCommandWhenSidecarHasBrewSource()
    {
        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var binaryPath = WriteAspireBinary(binaryDir.FullName);
        WriteSidecar(binaryDir.FullName, "brew");

        using var scope = BrewInstallDetection.UseProcessPathForTesting(binaryPath);

        Assert.True(BrewInstallDetection.IsRunningFromBrew());
        Assert.Equal("brew upgrade aspire", BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Theory]
    [InlineData("dotnet-tool")]
    [InlineData("npm")]
    [InlineData("script")]
    [InlineData("pr")]
    [InlineData("winget")]
    [InlineData("localhive")]
    [InlineData("unrecognized")]
    [InlineData("")]
    public void GetBrewUpdateCommand_ReturnsNullWhenSidecarSourceIsNotBrew(string source)
    {
        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var binaryPath = WriteAspireBinary(binaryDir.FullName);
        WriteSidecar(binaryDir.FullName, source);

        using var scope = BrewInstallDetection.UseProcessPathForTesting(binaryPath);

        Assert.False(BrewInstallDetection.IsRunningFromBrew());
        Assert.Null(BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Fact]
    public void GetBrewUpdateCommand_ReturnsNullWhenSidecarIsMissing()
    {
        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var binaryPath = WriteAspireBinary(binaryDir.FullName);

        using var scope = BrewInstallDetection.UseProcessPathForTesting(binaryPath);

        Assert.Null(BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Fact]
    public void GetBrewUpdateCommand_ReturnsNullWhenSidecarIsMalformed()
    {
        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var binaryPath = WriteAspireBinary(binaryDir.FullName);
        File.WriteAllText(System.IO.Path.Combine(binaryDir.FullName, ".aspire-install.json"), "{not valid json");

        using var scope = BrewInstallDetection.UseProcessPathForTesting(binaryPath);

        Assert.Null(BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetBrewUpdateCommand_ReturnsNullWhenProcessPathIsMissing(string? processPath)
    {
        using var scope = BrewInstallDetection.UseProcessPathForTesting(processPath);

        Assert.Null(BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Fact]
    public void GetBrewUpdateCommand_ResolvesSymlinkToFindSidecarInCellar()
    {
        // Mirrors the brew layout: <prefix>/bin/aspire is a symlink into the
        // Cellar where the sidecar lives next to the real binary. The detector
        // must follow the symlink before reading the sidecar; otherwise it
        // would look for the sidecar next to the link in `bin/` and miss it.
        if (OperatingSystem.IsWindows())
        {
            // Symlink creation on Windows requires elevation; the symlink-
            // resolution path is already exercised on Linux/macOS CI.
            return;
        }

        using var temp = new TestTempDirectory();
        var cellarBin = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "Cellar", "aspire", "1.0.0", "bin"));
        var realBinary = WriteAspireBinary(cellarBin.FullName);
        WriteSidecar(cellarBin.FullName, "brew");

        var linkBin = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var linkPath = System.IO.Path.Combine(linkBin.FullName, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.CreateSymbolicLink(linkPath, realBinary);

        using var scope = BrewInstallDetection.UseProcessPathForTesting(linkPath);

        Assert.Equal("brew upgrade aspire", BrewInstallDetection.GetBrewUpdateCommand());
    }

    [Fact]
    public void UseProcessPathForTesting_RestoresPreviousValueOnDispose()
    {
        // Establish a known baseline that does NOT detect brew, so the test result is
        // independent of the host's actual process path.
        using var baseline = BrewInstallDetection.UseProcessPathForTesting(null);
        Assert.False(BrewInstallDetection.IsRunningFromBrew());

        using var temp = new TestTempDirectory();
        var binaryDir = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "bin"));
        var binaryPath = WriteAspireBinary(binaryDir.FullName);
        WriteSidecar(binaryDir.FullName, "brew");

        using (BrewInstallDetection.UseProcessPathForTesting(binaryPath))
        {
            Assert.True(BrewInstallDetection.IsRunningFromBrew());
        }

        Assert.False(BrewInstallDetection.IsRunningFromBrew());
    }

    private static string WriteAspireBinary(string directory)
    {
        var binaryPath = System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? "aspire.exe" : "aspire");
        File.WriteAllText(binaryPath, string.Empty);
        return binaryPath;
    }

    private static void WriteSidecar(string directory, string source)
    {
        File.WriteAllText(
            System.IO.Path.Combine(directory, ".aspire-install.json"),
            $$"""{"source":"{{source}}"}""");
    }
}
