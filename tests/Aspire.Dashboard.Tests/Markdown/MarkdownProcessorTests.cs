// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Markdig;
using Xunit;

namespace Aspire.Dashboard.Tests.Markdown;

public class MarkdownProcessorTests
{
    [Fact]
    public void ToHtml_FencedCodeBlockBreaksIndentation_TrailingTextIsntACodeBlock()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            To restart the unhealthy container resources effectively, follow these steps:

            1. **Identify the Unhealthy Containers**:
               - From the resource graph, note the names of the unhealthy containers (e.g., `postgres-euaaphdw`, `basketcache-urvebuku`, `messaging-ffc632b9`).

            2. **Restart Containers Individually**:
               - Use the appropriate container management tool (e.g., Docker CLI, Docker Compose, Kubernetes) to restart each container. For Docker, you can use:
             ```bash

             docker restart <container_name>

             ```
                 Replace `<container_name>` with the actual container name (e.g., `postgres-euaaphdw`).

            3. **Verify Restart Success**
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        var count = Regex.Matches(html, Regex.Escape("code-block")).Count;
        Assert.Equal(1, count); // There should be one code block. There shouldn't be an indented code block.
    }

    [Fact]
    public void ToHtml_StartFencedCodeBlock_NoHtmlCode()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            Test:
            ```csha
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            """
            <p>Test:</p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToHtml_FencedCodeBlockEmpty_HasHtmlCode()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            Test:
            ```csharp
            ```
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Contains("</code>", html);
    }

    [Fact]
    public void ToHtml_ContainsFencedCodeBlock_HtmlHasCode()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            Test:
            ```csha
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            """
            <p>Test:</p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToHtml_IncompleteFencedCodeBlock_HtmlHasCode()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            Test:
            ```
            In code bl...
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Contains("</code>", html);
    }

    [Fact]
    public void ToHtml_ContainsFencedCodeBlockCsharp_HtmlHasCode()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            Test:
            ```csharp
            In code block.
            ```
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Contains("language-csharp", html);
        Assert.Contains("</code>", html);
    }

    [Fact]
    public void ToHtml_ContainsHtml_HtmlIgnored()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            **Waiting Resources:** <b>test</b>
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            """
            <p><strong>Waiting Resources:</strong> &lt;b&gt;test&lt;/b&gt;</p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToHtml_UrlWithJavaScript_UrlRemoved()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            """
            [malicious](javascript:alert('hi'))
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            """
            <p><a href="">malicious</a></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("http://localhost:8080")]
    [InlineData("https://localhost:8081")]
    [InlineData("mailto:test@test.com")]
    public void ToHtml_UrlWithValidSchemes_LinkAdded(string url)
    {
        // Arrange
        var processor = CreateMarkdownProcessor(safeUrlSchemes: MarkdownHelpers.SafeUrlSchemes);

        var markdown =
            $"""
            [test]({url})
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            $"""
            <p><a href="{url}" target="_blank" rel="noopener noreferrer nofollow">test</a></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("vscode://localhost:8080")]
    [InlineData("spotify://localhost:8080")]
    public void ToHtml_UrlWithInvalidSchemes_LinkNotAdded(string url)
    {
        // Arrange
        var processor = CreateMarkdownProcessor(safeUrlSchemes: MarkdownHelpers.SafeUrlSchemes);

        var markdown =
            $"""
            [test]({url})
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            $"""
            <p><a href="">test</a></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("http://contoso.com:8080", "http://contoso.com:8080")]
    [InlineData("https://contoso.com:8081", "https://contoso.com:8081")]
    [InlineData("mailto:test@test.com", "test@test.com")]
    public void ToHtml_AutoLinkWithValidSchemes_LinkAdded(string url, string text)
    {
        // Arrange
        var processor = CreateMarkdownProcessor(safeUrlSchemes: MarkdownHelpers.SafeUrlSchemes);

        var markdown =
            $"""
            {url}
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            $"""
            <p><a href="{url}" target="_blank" rel="noopener noreferrer nofollow">{text}</a></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("https://localhost", "https://localhost")]
    public void ToHtml_Localhost_LinkAdded(string url, string text)
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            $"""
            {url}
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            $"""
            <p><a href="{url}" target="_blank" rel="noopener noreferrer nofollow">{text}</a></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Fact]
    public void ToHtml_UrlInsideCodeWithExtra_NoLink()
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            $"""
            `http://localhost:8080 extra`
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(
            $"""
            <p><code>http://localhost:8080 extra</code></p>
            """, html.Trim(), ignoreLineEndingDifferences: true);
    }

    [Theory]
    [InlineData("# Header", "<h4>Header</h4>")]
    [InlineData("## Header", "<h4>Header</h4>")]
    [InlineData("### Header", "<h5>Header</h5>")]
    [InlineData("#### Header", "<h5>Header</h5>")]
    [InlineData("##### Header", "<h6>Header</h6>")]
    [InlineData("###### Header", "<h6>Header</h6>")]
    public void ToHtml_Header_LevelAdjusted(string header, string expected)
    {
        // Arrange
        var processor = CreateMarkdownProcessor();

        var markdown =
            $"""
            {header}
            """;

        // Act
        var html = processor.ToHtml(markdown, inCompleteDocument: true);

        // Assert
        Assert.Equal(expected, html.Trim(), ignoreLineEndingDifferences: true);
    }

    internal static MarkdownProcessor CreateMarkdownProcessor(HashSet<string>? safeUrlSchemes = null, List<IMarkdownExtension>? extensions = null)
    {
        return new MarkdownProcessor(new TestStringLocalizer<ControlsStrings>(), safeUrlSchemes, extensions ?? []);
    }
}
