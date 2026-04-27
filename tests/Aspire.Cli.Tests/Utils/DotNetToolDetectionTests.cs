// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class DotNetToolDetectionTests
{
    [Theory]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/linux-x64/tools/net10.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/Aspire.Cli/10.0.0/linux-arm64/tools/net10.0/linux-arm64/aspire")]
    [InlineData(@"C:\Users\test\.dotnet\tools\.store\aspire.cli\10.0.0\win-x64\tools\net10.0\win-x64\aspire.exe")]
    public void IsRunningAsDotNetTool_ReturnsTrueForAspireCliNativeAotToolStorePath(string processPath)
    {
        Assert.True(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dotnet")]
    [InlineData("dotnet.exe")]
    [InlineData("/home/test/.aspire/bin/aspire")]
    [InlineData("/home/test/.dotnet/tools/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/other.cli/10.0.0/linux-x64/tools/net10.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/linux-x64/tools/net10.0/osx-arm64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/linux-x64/tools/net9.0/linux-x64/aspire")]
    [InlineData("/home/test/.dotnet/tools/.store/aspire.cli/10.0.0/linux-x64/tools/net10.0/linux-x64/other")]
    public void IsRunningAsDotNetTool_ReturnsFalseForNonNativeAotToolStorePath(string? processPath)
    {
        Assert.False(DotNetToolDetection.IsRunningAsDotNetTool(processPath));
    }
}
