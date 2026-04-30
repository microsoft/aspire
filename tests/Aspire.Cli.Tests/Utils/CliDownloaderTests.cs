// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class CliDownloaderTests
{
    [Theory]
    [InlineData("https://aka.ms/dotnet/9/aspire/ga/daily/aspire-cli-linux-x64.tar.gz", "the stable channel", "aspire-cli-linux-x64.tar.gz from the stable channel")]
    [InlineData("https://aka.ms/dotnet/9/aspire/daily/aspire-cli-osx-arm64.tar.gz", "the daily channel", "aspire-cli-osx-arm64.tar.gz from the daily channel")]
    [InlineData("https://aka.ms/dotnet/9/aspire/rc/daily/aspire-cli-win-x64.zip", "the staging channel", "aspire-cli-win-x64.zip from the staging channel")]
    [InlineData("https://ci.dot.net/public/aspire//13.2.0-preview.1.25366.3/aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz", "the stable channel", "aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz from the stable channel")]
    [InlineData("https://ci.dot.net/public-checksums/aspire/13.2.0-preview.1.25366.3/aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz.sha512", "the stable channel", "aspire-cli-linux-x64-13.2.0-preview.1.25366.3.tar.gz.sha512 from the stable channel")]
    [InlineData("https://example.com/downloads/aspire-cli-linux-x64.tar.gz?sig=123", null, "aspire-cli-linux-x64.tar.gz")]
    [InlineData("not a url", "the stable channel", "not a url")]
    public void GetDownloadDescriptor_ReturnsCompactDescriptor(string url, string? source, string expectedDescriptor)
    {
        var descriptor = CliDownloader.GetDownloadDescriptor(url, source);

        Assert.Equal(expectedDescriptor, descriptor);
        Assert.DoesNotContain("dotnet", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ga/", descriptor, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rc/", descriptor, StringComparison.OrdinalIgnoreCase);
    }
}
