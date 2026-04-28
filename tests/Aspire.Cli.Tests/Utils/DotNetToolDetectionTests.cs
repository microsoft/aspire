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
    [InlineData(@"C:\Users\test\.dotnet\tools\.store\aspire.cli\10.0.0\aspire.cli.win-x64\10.0.0\tools\any\win-x64\aspire.exe")]
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
}
