// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class PackageSourceRedactorTests
{
    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", "https://api.nuget.org/v3/index.json")]
    [InlineData("  https://api.nuget.org/v3/index.json  ", "https://api.nuget.org/v3/index.json")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json", "https://***@feed.example.com/v3/index.json")]
    [InlineData("https://feed.example.com/v3/index.json?sig=secret", "https://feed.example.com/v3/index.json")]
    [InlineData("https://feed.example.com/v3/index.json#fragment", "https://feed.example.com/v3/index.json")]
    [InlineData("https://user:pat@feed.example.com/v3/index.json?sig=secret#fragment", "https://***@feed.example.com/v3/index.json")]
    public void RedactForDisplay_RedactsHttpSources(string source, string expected)
    {
        Assert.Equal(expected, PackageSourceRedactor.RedactForDisplay(source));
    }

    [Theory]
    [InlineData("https://user:p@ss@host/path")]
    [InlineData("https://user:p#word@host/")]
    [InlineData("http://foo bar/path")]
    [InlineData("  HTTPS://user:p@ss@host/path  ")]
    public void RedactForDisplay_FailsClosedForMalformedHttpSources(string source)
    {
        Assert.Equal("<unparseable http source>", PackageSourceRedactor.RedactForDisplay(source));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("/tmp/aspire-packages", "/tmp/aspire-packages")]
    [InlineData(@"C:\packages", @"C:\packages")]
    [InlineData("file:///tmp/aspire-packages", "file:///tmp/aspire-packages")]
    [InlineData("/tmp/aspire/some path with [brackets]", "/tmp/aspire/some path with [brackets]")]
    public void RedactForDisplay_PreservesNonHttpSources(string source, string expected)
    {
        Assert.Equal(expected, PackageSourceRedactor.RedactForDisplay(source));
    }

    [Theory]
    [InlineData(
        "  HTTPS://user:secret@HOST.example/Feed?sig=secret  ",
        "HTTPS://user:secret@HOST.example/Feed?sig=secret",
        "Restore failed for https://***@host.example/Feed.")]
    [InlineData(
        "  HTTPS://user:secret@HOST.example/Feed?sig=secret  ",
        "https://user:secret@host.example/Feed?sig=secret",
        "Restore failed for https://***@host.example/Feed.")]
    [InlineData(
        "  https://user:p#word@host/  ",
        "https://user:p#word@host/",
        "Restore failed for <unparseable http source>.")]
    public void RedactOccurrences_RedactsNormalizedDiagnosticSpellings(
        string source,
        string diagnosticSpelling,
        string expected)
    {
        var result = PackageSourceRedactor.RedactOccurrences(
            $"Restore failed for {diagnosticSpelling}.",
            [source]);

        Assert.Equal(expected, result);
    }
}
