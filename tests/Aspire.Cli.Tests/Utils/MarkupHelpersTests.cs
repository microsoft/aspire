// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Tests.Utils;

public class MarkupHelpersTests
{
    [Theory]
    [InlineData("https://example.com", null, "[link]https://example.com[/]")]
    [InlineData("https://example.com", "Example", "[link=https://example.com]Example[/]")]
    [InlineData("https://example.com", "https://example.com", "[link]https://example.com[/]")] // title == link treated as no-title
    [InlineData("http://[::1]:8080/dashboard", null, "[link]http://[[::1]]:8080/dashboard[/]")]
    [InlineData("http://[::1]:8080/dashboard", "Dashboard", "[link=http://[[::1]]:8080/dashboard]Dashboard[/]")]
    [InlineData("http://[fe80::1%25eth0]:5000", null, "[link]http://[[fe80::1%25eth0]]:5000[/]")]
    [InlineData("http://[2001:db8::1]:18888/traces", "Traces", "[link=http://[[2001:db8::1]]:18888/traces]Traces[/]")]
    [InlineData("https://example.com/path?a=1&b=2", null, "[link]https://example.com/path?a=1&b=2[/]")]
    [InlineData("https://example.com/path?a=1&b=2", "Query Link", "[link=https://example.com/path?a=1&b=2]Query Link[/]")]
    public void SafeLink_WhenSupportsLinks_ReturnsLinkMarkup(string link, string? title, string expected)
    {
        var result = MarkupHelpers.SafeLink(supportsLinks: true, link, title);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://example.com", null, "https://example.com")]
    [InlineData("https://example.com", "Example", "Example (https://example.com)")]
    [InlineData("https://example.com", "https://example.com", "https://example.com")] // title == link treated as no-title
    [InlineData("http://[::1]:8080/dashboard", null, "http://[[::1]]:8080/dashboard")]
    [InlineData("http://[::1]:8080/dashboard", "Dashboard", "Dashboard (http://[[::1]]:8080/dashboard)")]
    [InlineData("http://[fe80::1%25eth0]:5000", null, "http://[[fe80::1%25eth0]]:5000")]
    [InlineData("http://[2001:db8::1]:18888/traces", "Traces", "Traces (http://[[2001:db8::1]]:18888/traces)")]
    [InlineData("https://example.com/path?a=1&b=2", null, "https://example.com/path?a=1&b=2")]
    [InlineData("https://example.com/path?a=1&b=2", "Query Link", "Query Link (https://example.com/path?a=1&b=2)")]
    public void SafeLink_WhenNoLinkSupport_ReturnsPlainText(string link, string? title, string expected)
    {
        var result = MarkupHelpers.SafeLink(supportsLinks: false, link, title);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("https://example.com/path?a=1&b=2", null, "https://example.com/path?a=1&b=2")]
    [InlineData("http://[::1]:8080/dashboard", null, "http://[::1]:8080/dashboard")]
    [InlineData("http://[2001:db8::1]:18888/traces", "Traces", "Traces")]
    [InlineData("http://[fe80::1%25eth0]:5000/resource", "My [Resource]", "My [Resource]")]
    [InlineData("https://example.com", "Title with [brackets]", "Title with [brackets]")]
    public void SafeLink_WhenSupportsLinks_ProducesValidMarkup(string link, string? title, string expectedText)
    {
        var result = MarkupHelpers.SafeLink(supportsLinks: true, link, title);

        var output = new StringBuilder();
        var console = CreateAnsiConsole(output, ansi: true);
        console.MarkupLine(result);

        var rendered = output.ToString().Trim();
        // OSC 8 format: ESC]8;id=N;url ESC\ text ESC]8;; ESC\
        var escapedLink = Regex.Escape(link);
        var escapedText = Regex.Escape(expectedText);
        var pattern = $"\\x1b]8;id=\\d+;{escapedLink}\\x1b\\\\{escapedText}\\x1b]8;;\\x1b\\\\";
        Assert.Matches(pattern, rendered);
    }

    [Theory]
    [InlineData("https://example.com/path?a=1&b=2", null, "https://example.com/path?a=1&b=2")]
    [InlineData("http://[::1]:8080/dashboard", null, "http://[::1]:8080/dashboard")]
    [InlineData("http://[2001:db8::1]:18888/traces", "Traces", "Traces (http://[2001:db8::1]:18888/traces)")]
    [InlineData("http://[fe80::1%25eth0]:5000/resource", "My [Resource]", "My [Resource] (http://[fe80::1%25eth0]:5000/resource)")]
    [InlineData("https://example.com", "Title with [brackets]", "Title with [brackets] (https://example.com)")]
    public void SafeLink_WhenNoLinkSupport_ProducesValidMarkup(string link, string? title, string expectedText)
    {
        var result = MarkupHelpers.SafeLink(supportsLinks: false, link, title);

        var output = new StringBuilder();
        var console = CreateAnsiConsole(output, ansi: false);
        console.MarkupLine(result);

        var rendered = output.ToString().Trim();
        Assert.Equal(expectedText, rendered);
    }

    private static IAnsiConsole CreateAnsiConsole(StringBuilder output, bool ansi)
    {
        var settings = new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansi ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        };
        var console = AnsiConsole.Create(settings);
        console.Profile.Width = int.MaxValue;
        if (ansi)
        {
            console.Profile.Capabilities.Links = true;
        }
        return console;
    }

    [Fact]
    public void SafeFileLink_WithEmptyPath_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MarkupHelpers.SafeFileLink(supportsLinks: true, string.Empty));
        Assert.Equal(string.Empty, MarkupHelpers.SafeFileLink(supportsLinks: false, string.Empty));
    }

    [Fact]
    public void SafeFileLink_WhenNoLinkSupport_ReturnsEscapedPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "logs", "cli [Dev].log");

        var result = MarkupHelpers.SafeFileLink(supportsLinks: false, path);

        Assert.Equal(path.EscapeMarkup(), result);
        Assert.DoesNotContain("[link", result);
    }

    [Fact]
    public void SafeFileLink_WhenSupportsLinks_BuildsLinkMarkupWithFileUri()
    {
        var path = Path.Combine(Path.GetTempPath(), "logs", "aspire.log");

        var result = MarkupHelpers.SafeFileLink(supportsLinks: true, path);

        var expectedUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        Assert.Equal($"[link={expectedUri}]{path.EscapeMarkup()}[/]", result);
    }

    [Fact]
    public void SafeFileLink_WhenSupportsLinks_PercentEncodesBracketsInUri()
    {
        // Bracket characters in file paths are not percent-encoded by Uri.AbsoluteUri,
        // but they would otherwise be parsed by Spectre.Console as the start of a new
        // markup tag inside [link=...]. SafeFileLink must encode them to keep both the
        // OSC 8 hyperlink and the surrounding markup well-formed.
        var path = Path.Combine(Path.GetTempPath(), "logs", "cli [Dev].log");

        var result = MarkupHelpers.SafeFileLink(supportsLinks: true, path);

        var expectedUri = new Uri(Path.GetFullPath(path)).AbsoluteUri
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal);
        Assert.Contains("%5B", expectedUri);
        Assert.Contains("%5D", expectedUri);
        Assert.Equal($"[link={expectedUri}]{path.EscapeMarkup()}[/]", result);
    }

    [Fact]
    public void SafeFileLink_WhenSupportsLinks_ProducesValidMarkup()
    {
        var path = Path.Combine(Path.GetTempPath(), "logs", "cli [Dev].log");
        var result = MarkupHelpers.SafeFileLink(supportsLinks: true, path);

        var output = new StringBuilder();
        var console = CreateAnsiConsole(output, ansi: true);
        console.MarkupLine(result);

        var rendered = output.ToString().Trim();
        var fileUri = new Uri(Path.GetFullPath(path)).AbsoluteUri
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal);
        var pattern = $"\\x1b]8;id=\\d+;{Regex.Escape(fileUri)}\\x1b\\\\{Regex.Escape(path)}\\x1b]8;;\\x1b\\\\";
        Assert.Matches(pattern, rendered);
    }
}
