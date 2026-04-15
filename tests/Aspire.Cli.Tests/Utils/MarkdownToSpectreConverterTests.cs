// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class MarkdownToSpectreConverterTests
{
    [Fact]
    public void ConvertToSpectre_WithEmptyString_ReturnsEmpty()
    {
        // Arrange
        var markdown = "";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("", result);
    }

    [Fact]
    public void ConvertToSpectre_WithNull_ReturnsNull()
    {
        // Arrange
        string? markdown = null;

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown!);

        // Assert
        Assert.Equal(markdown, result);
    }

    [Fact]
    public void ConvertToSpectre_WithPlainText_ReturnsUnchanged()
    {
        // Arrange
        var markdown = "This is plain text without any markdown.";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("This is plain text without any markdown.", result);
    }

    [Theory]
    [InlineData("# Header 1", "[bold green]Header 1[/]")]
    [InlineData("## Header 2", "[bold blue]Header 2[/]")]
    [InlineData("### Header 3", "[bold yellow]Header 3[/]")]
    [InlineData("#### Header 4", "[bold]Header 4[/]")]
    [InlineData("##### Header 5", "[bold]Header 5[/]")]
    [InlineData("###### Header 6", "[bold]Header 6[/]")]
    public void ConvertToSpectre_WithHeaders_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("**bold text**", "[bold]bold text[/]")]
    [InlineData("__also bold__", "[bold]also bold[/]")]
    [InlineData("This is **bold** and this is not.", "This is [bold]bold[/] and this is not.")]
    public void ConvertToSpectre_WithBoldText_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("*italic text*", "[italic]italic text[/]")]
    [InlineData("_also italic_", "[italic]also italic[/]")]
    [InlineData("This is *italic* and this is not.", "This is [italic]italic[/] and this is not.")]
    public void ConvertToSpectre_WithItalicText_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("`inline code`", "[grey][bold]inline code[/][/]")]
    [InlineData("This is `code` in text.", "This is [grey][bold]code[/][/] in text.")]
    public void ConvertToSpectre_WithInlineCode_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("[link text](https://example.com)", "[cyan][link=https://example.com]link text[/][/]")]
    [InlineData("Visit [GitHub](https://github.com) for more info.", "Visit [cyan][link=https://github.com]GitHub[/][/] for more info.")]
    [InlineData("[CreateBuilder(string[])](https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)", "[cyan][link=https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string]CreateBuilder(string[[]])[/][/]")]
    public void ConvertToSpectre_WithLinks_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertLinksToPlainText_WithBracketedLinkText_ConvertsCorrectly()
    {
        var result = MarkdownToSpectreConverter.ConvertLinksToPlainText(
            "[CreateBuilder(string[])](https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)");

        Assert.Equal(
            "CreateBuilder(string[]) (https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)",
            result);
    }

    [Fact]
    public void ConvertToSpectre_WithComplexMarkdown_ConvertsAllElements()
    {
        // Arrange
        var markdown = @"# Main Header
This is **bold** and *italic* text with `inline code`.
## Sub Header
Visit [GitHub](https://github.com) for more information.
### Small Header
Some more text.";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        var expected = @"[bold green]Main Header[/]
This is [bold]bold[/] and [italic]italic[/] text with [grey][bold]inline code[/][/].
[bold blue]Sub Header[/]
Visit [cyan][link=https://github.com]GitHub[/][/] for more information.
[bold yellow]Small Header[/]
Some more text.";
        // Normalize line endings in expected string to match converter output
        expected = expected.Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToSpectre_WithNestedFormatting_HandlesCorrectly()
    {
        // Arrange - test that ** inside * doesn't break things
        var markdown = "This should not break: **bold** and *italic*";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("This should not break: [bold]bold[/] and [italic]italic[/]", result);
    }

    [Fact]
    public void ConvertToSpectre_WithReferenceLinks_EscapesSquareBrackets()
    {
        // Arrange - test reference style links that should be escaped
        var markdown = "Reference style: [ref link][id1] and another [second][id2].";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("Reference style: [[ref link]][[id1]] and another [[second]][[id2]].", result);
    }

    [Fact]
    public void ConvertToSpectre_WithMixedLinks_HandlesCorrectly()
    {
        // Arrange - test mix of inline links (should convert) and reference links (should escape)
        var markdown = "Inline [GitHub](https://github.com) and reference [docs][ref1].";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("Inline [cyan][link=https://github.com]GitHub[/][/] and reference [[docs]][[ref1]].", result);
    }

    [Theory]
    [InlineData("[standalone]", "[[standalone]]")]
    [InlineData("Text [bracket] more text", "Text [[bracket]] more text")]
    [InlineData("[multiple] [brackets] [here]", "[[multiple]] [[brackets]] [[here]]")]
    public void ConvertToSpectre_WithStandaloneBrackets_EscapesCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("![alt text](https://example.com/image.png)", "")]
    [InlineData("![](https://example.com/image.png)", "")]
    [InlineData("![alt with spaces](https://example.com/image.jpg)", "")]
    [InlineData("Text before ![image](https://example.com/pic.png) text after", "Text before  text after")]
    public void ConvertToSpectre_WithImages_OmitsImages(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToSpectre_WithMultipleImages_OmitsAllImages()
    {
        // Arrange
        var markdown = "Here is ![first image](https://example.com/1.png) and ![second image](https://example.com/2.jpg) in text.";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("Here is  and  in text.", result);
    }

    [Fact]
    public void ConvertToSpectre_WithImagesAndLinks_ProcessesCorrectly()
    {
        // Arrange - test that images are removed but links are preserved
        var markdown = "Visit [GitHub](https://github.com) and see this ![screenshot](https://example.com/pic.png) for details.";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal("Visit [cyan][link=https://github.com]GitHub[/][/] and see this  for details.", result);
    }

    [Fact]
    public void ConvertToSpectre_WithImagesInComplexMarkdown_HandlesCorrectly()
    {
        // Arrange
        var markdown = @"# Documentation
This is **important** information with an image: ![diagram](https://example.com/diagram.png)

Visit [our site](https://example.com) for more details.";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        var expected = @"[bold green]Documentation[/]
This is [bold]important[/] information with an image: 

Visit [cyan][link=https://example.com]our site[/][/] for more details.";
        // Normalize line endings in expected string to match converter output
        expected = expected.Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("~~strikethrough text~~", "[strikethrough]strikethrough text[/]")]
    [InlineData("This is ~~deleted~~ text.", "This is [strikethrough]deleted[/] text.")]
    [InlineData("Multiple ~~words~~ and ~~more~~", "Multiple [strikethrough]words[/] and [strikethrough]more[/]")]
    public void ConvertToSpectre_WithStrikethrough_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("```\ncode block\n```", "[grey]code block[/]")]
    [InlineData("```\nmulti\nline\ncode\n```", "[grey]multi\nline\ncode[/]")]
    [InlineData("Text before ```code``` after", "Text before [grey]code[/] after")]
    public void ConvertToSpectre_WithCodeBlocks_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("> quoted text", "[italic grey]quoted text[/]")]
    [InlineData("> This is a quote", "[italic grey]This is a quote[/]")]
    [InlineData("Normal text\n> quoted line\nMore text", "Normal text\n[italic grey]quoted line[/]\nMore text")]
    public void ConvertToSpectre_WithQuotedText_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToSpectre_WithAllNewFeatures_ConvertsCorrectly()
    {
        // Arrange
        var markdown = @"#### Header 4
> This is a quoted line
Some ~~strikethrough~~ text with ```inline code block```.
##### Header 5
###### Header 6";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        var expected = @"[bold]Header 4[/]
[italic grey]This is a quoted line[/]
Some [strikethrough]strikethrough[/] text with [grey]inline code block[/].
[bold]Header 5[/]
[bold]Header 6[/]".Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToSpectre_WithMultilineQuotesWithEmptyLines_ConvertsAllLines()
    {
        // Arrange
        var markdown = @"> Line 1
> 
> Line 2";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        var expected = @"[italic grey]Line 1[/]
[italic grey][/]
[italic grey]Line 2[/]".Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("> ", "[italic grey][/]")]
    [InlineData(">", "[italic grey][/]")]
    [InlineData("> text", "[italic grey]text[/]")]
    public void ConvertToSpectre_WithVariousQuoteFormats_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("```bash\nexport APP_NAME=\"your-app-name\"\n```", "[grey]export APP_NAME=\"your-app-name\"[/]")]
    [InlineData("```javascript\nconsole.log('hello');\n```", "[grey]console.log('hello');[/]")]
    [InlineData("```\nno language specified\n```", "[grey]no language specified[/]")]
    [InlineData("```python\nprint('test')\nprint('multiline')\n```", "[grey]print('test')\nprint('multiline')[/]")]
    public void ConvertToSpectre_WithCodeBlocksWithLanguages_RemovesLanguageNames(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToSpectre_WithComplexMultilineQuotesAndCodeBlocks_ConvertsCorrectly()
    {
        // Arrange
        var markdown = @"# Instructions
> This is important
> 
> Please follow these steps:

```bash
cd /path/to/project
npm install
```

> That's all!";

        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        var expected = @"[bold green]Instructions[/]
[italic grey]This is important[/]
[italic grey][/]
[italic grey]Please follow these steps:[/]

[grey]cd /path/to/project
npm install[/]

[italic grey]That's all![/]".Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }
}
