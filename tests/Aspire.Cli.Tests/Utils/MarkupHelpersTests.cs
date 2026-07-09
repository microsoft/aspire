// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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
        TerminalLinkAssert.ContainsLink(rendered, link, expectedText);
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
    public void SafeFileLink_WhenSupportsLinks_EscapesBracketsInUriMarkup()
    {
        var path = Path.Combine(Path.GetTempPath(), "logs", "cli [Dev].log");

        var result = MarkupHelpers.SafeFileLink(supportsLinks: true, path);

        var expectedUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        Assert.Equal($"[link={expectedUri.EscapeMarkup()}]{path.EscapeMarkup()}[/]", result);
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
        var fileUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        TerminalLinkAssert.ContainsLink(rendered, fileUri, path);
    }

    [Fact]
    public void SafeFileLink_LongPath_DoesNotWrapIncorrectly()
    {
        // Regression test: Spectre.Console 0.57.0 had a wrapping bug (spectreconsole/spectre.console#2152)
        // where long file paths rendered via links would be incorrectly broken across lines.
        // This test uses a narrow console width to verify the fix in 0.57.2.
        var path = Path.Combine(Path.GetTempPath(), "aspire", "logs", "aspire-cli-20260101T120000Z.log");
        var result = MarkupHelpers.SafeFileLink(supportsLinks: true, path);

        var output = new StringBuilder();
        var settings = new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        };
        var console = AnsiConsole.Create(settings);
        console.Profile.Width = 40;
        console.Profile.Capabilities.Links = true;

        console.MarkupLine(result);

        var rendered = output.ToString();
        var fileUri = new Uri(Path.GetFullPath(path)).AbsoluteUri;

        // The link should be present and the file name should appear as a contiguous string
        // (not split by wrapping) within the OSC 8 hyperlink sequence.
        TerminalLinkAssert.ContainsLink(rendered, fileUri, path);
    }

    [Fact]
    public void SafeFileLink_LongPath_NoLinks_DoesNotWrapIncorrectly()
    {
        // Same wrapping regression test but for terminals without link support.
        var path = Path.Combine(Path.GetTempPath(), "aspire", "logs", "aspire-cli-20260101T120000Z.log");
        var result = MarkupHelpers.SafeFileLink(supportsLinks: false, path);

        var output = new StringBuilder();
        var settings = new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter(output)),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        };
        var console = AnsiConsole.Create(settings);
        console.Profile.Width = 40;

        console.MarkupLine(result);

        // The rendered output should contain the full path without spurious line breaks
        // splitting the filename itself. Line wrapping is acceptable at word boundaries
        // but should not split in the middle of the file name.
        var rendered = output.ToString();
        Assert.Contains("aspire-cli-20260101T120000Z.log", rendered);
    }
}
