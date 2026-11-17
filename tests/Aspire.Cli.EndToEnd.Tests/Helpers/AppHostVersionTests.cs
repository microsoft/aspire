// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

public class AppHostVersionTests
{
    [Theory]
    [InlineData("#:package Aspire.Hosting.AppHost@17.0.0-pr.20867+metadata\n#:package Aspire.Hosting.Dotnet@17.0.0", "17.0.0-pr.20867+metadata")]
    [InlineData(" \t#:package Aspire.Hosting.AppHost@17.0.0 \r\n", "17.0.0")]
    [InlineData("#:sdk Aspire.AppHost.Sdk@16.0.0\n", "16.0.0")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"17.0.0\" /></ItemGroup></Project>", "17.0.0")]
    [InlineData("<Project Sdk=\"Aspire.AppHost.Sdk/16.0.0\" />", "16.0.0")]
    [InlineData("<Project Sdk=\"Aspire.AppHost.Sdk/16.0.0\"><ItemGroup><PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"17.0.0\" /></ItemGroup></Project>", "17.0.0")]
    public void ReadsExplicitPackageAndRetainedSdkVersions(string content, string expected)
    {
        Assert.Equal(expected, CliE2ETestHelpers.GetAppHostVersion(content));
    }

    [Theory]
    [InlineData("// #:package Aspire.Hosting.AppHost@17.0.0")]
    [InlineData("#:package Aspire.Hosting.AppHost@17.0.0 trailing")]
    [InlineData("#:package Aspire.Hosting.Dotnet@17.0.0")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\" />")]
    public void RejectsMissingOrInvalidAppHostVersion(string content)
    {
        Assert.Throws<InvalidOperationException>(() => CliE2ETestHelpers.GetAppHostVersion(content));
    }
}
