// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class DotNetToolDetectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsRunningAsDotNetTool_ReturnsFalse_WhenProcessPathIsMissing(string? processPath)
    {
        Assert.False(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    [Theory]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    public void IsRunningAsDotNetTool_ReturnsTrue_WhenProcessIsDotnetHost(string fileName)
    {
        Assert.True(DotNetToolDetection.IsRunningAsDotNetTool(Path.Combine("usr", "bin", fileName)));
    }

    [Fact]
    public void IsRunningAsDotNetTool_ReturnsTrue_WhenProcessIsUnderToolStore()
    {
        var processPath = Path.Combine(
            "home",
            "test",
            ".dotnet",
            "tools",
            ".store",
            "aspire.cli",
            "13.2.0",
            "aspire.cli",
            "13.2.0",
            "tools",
            "net10.0",
            "linux-x64",
            "aspire");

        Assert.True(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    [Fact]
    public void IsRunningAsDotNetTool_ReturnsFalse_WhenProcessIsStandaloneCli()
    {
        var processPath = Path.Combine("home", "test", ".aspire", "bin", "aspire");

        Assert.False(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }
}
