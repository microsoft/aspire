// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class DotNetToolDetectionTests
{
    [Theory]
    [InlineData("/home/test/.dotnet/tools/aspire")]
    [InlineData(@"C:\Users\test\.dotnet\tools\aspire.exe")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/any/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/net10.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/Aspire.Cli.linux-arm64/10.0.0/tools/any/linux-arm64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli/10.0.0/tools/net10.0/any/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/net10.0/linux-x64/future-layout-segment/aspire")]
    [InlineData(@"C:\Users\test\.dotnet\tools\.store\aspire.cli\10.0.0\aspire.cli.win-x64\10.0.0\tools\any\win-x64\aspire.exe")]
    public void IsRunningAsDotNetTool_ReturnsTrueForAspireCliNativeAotToolStorePath(string processPath)
    {
        Assert.True(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    [Fact]
    public void IsRunningAsDotNetTool_ReturnsTrueForCustomToolPathWithSiblingStore()
    {
        using var tempDirectory = new TestTempDirectory();
        var toolPath = Path.Combine(tempDirectory.Path, "custom tool path");
        var processPath = Path.Combine(toolPath, GetAspireExecutableName());
        var storeExecutablePath = Path.Combine(
            toolPath,
            ".store",
            "aspire.cli",
            "10.0.0",
            "aspire.cli.linux-x64",
            "10.0.0",
            "tools",
            "net10.0",
            "linux-x64",
            GetAspireExecutableName());

        Directory.CreateDirectory(toolPath);
        Directory.CreateDirectory(Path.GetDirectoryName(storeExecutablePath)!);
        File.WriteAllText(processPath, string.Empty);
        File.WriteAllText(storeExecutablePath, string.Empty);

        Assert.True(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
        Assert.Equal($"dotnet tool update --tool-path \"{toolPath}\" Aspire.Cli", DotNetToolDetection.GetDotNetToolUpdateCommand(processPath));
    }

    [Fact]
    public void GetDotNetToolUpdateCommand_ReturnsToolPathCommandForCustomToolStorePath()
    {
        using var tempDirectory = new TestTempDirectory();
        var toolPath = Path.Combine(tempDirectory.Path, "custom tool path");
        var processPath = Path.Combine(
            toolPath,
            ".store",
            "aspire.cli",
            "10.0.0",
            "aspire.cli.linux-x64",
            "10.0.0",
            "tools",
            "net10.0",
            "linux-x64",
            GetAspireExecutableName());

        Assert.Equal($"dotnet tool update --tool-path \"{toolPath}\" Aspire.Cli", DotNetToolDetection.GetDotNetToolUpdateCommand(processPath));
    }

    [Theory]
    [InlineData("/home/test/.dotnet/tools/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/any/linux-x64/aspire")]
    public void GetDotNetToolUpdateCommand_ReturnsGlobalCommandForGlobalToolPath(string processPath)
    {
        Assert.Equal("dotnet tool update -g Aspire.Cli", DotNetToolDetection.GetDotNetToolUpdateCommand(processPath));
    }

    [Fact]
    public void IsRunningAsDotNetTool_ReturnsFalseForCustomToolPathWithoutSiblingStore()
    {
        using var tempDirectory = new TestTempDirectory();
        var processPath = Path.Combine(tempDirectory.Path, GetAspireExecutableName());
        File.WriteAllText(processPath, string.Empty);

        Assert.False(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("/home/test/.aspire/bin/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/other.cli/10.0.0/linux-x64/tools/net10.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/any/osx-arm64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/net9.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.linux-x64/10.0.0/tools/any/linux-x64/other")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/aspire.cli.not-a-rid/10.0.0/tools/any/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli.linux-x64/10.0.0/aspire.cli.linux-x64/10.0.0/tools/any/linux-x64/aspire")]
    public void IsRunningAsDotNetTool_ReturnsFalseForNonNativeAotToolStorePath(string? processPath)
    {
        Assert.False(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    private static string GetAspireExecutableName()
    {
        return OperatingSystem.IsWindows() ? "aspire.exe" : "aspire";
    }
}
