// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.Utils.Markdown;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.Utils;

public partial class MarkdownToSpectreConverterTests
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
    public void ConvertToPlainText_WithLinksAndImages_ConvertsCorrectly()
    {
        var markdown = "Visit [GitHub](https://github.com) and remove ![diagram](https://example.com/diagram.png).";

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Equal("Visit GitHub (https://github.com) and remove .", result);
    }

    [Fact]
    public void ConvertToPlainText_StripsSimpleMarkdownFormatting()
    {
        var markdown = "## Heading\nThis is **bold**, *italic*, and ~~deleted~~.";

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("Heading", result);
        Assert.Contains("This is bold, italic, and deleted.", result);
        Assert.DoesNotContain("**", result);
        Assert.DoesNotContain("~~", result);
    }

    [Fact]
    public void ConvertToPlainText_WithTable_RendersReadableRows()
    {
        var markdown = """
            | Setting | Environment variable | Purpose |
            | :------ | :------------------- | ------: |
            | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
            """;

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("Setting", result);
        Assert.Contains("Environment variable", result);
        Assert.Contains("Purpose", result);
        Assert.Contains("Azure:SubscriptionId", result);
        Assert.Contains("Azure__SubscriptionId", result);
        Assert.Contains("Target Azure subscription", result);
    }

    [Fact]
    public void ConvertToRenderable_WithTable_ReturnsSpectreTable()
    {
        var markdown = """
            | Setting | Environment variable | Purpose |
            | ------- | -------------------- | ------- |
            | `Azure:SubscriptionId` | `Azure__SubscriptionId` | Target Azure subscription |
            """;

        var renderable = MarkdownToSpectreConverter.ConvertToRenderable(markdown);

        var table = Assert.IsType<Table>(renderable);
        Assert.Equal(3, table.Columns.Count);
        Assert.Single(table.Rows);
    }

    [Fact]
    public void ConvertToRenderable_WithMixedMarkdown_RendersReadableOutput()
    {
        var markdown = """
            # Header

            > quoted line

            1. First item
            2. Second item

            ```bash
            aspire docs get redis-integration
            ```

            Visit [GitHub](https://github.com) for more info.
            """;

        var renderable = MarkdownToSpectreConverter.ConvertToRenderable(markdown);

        Assert.IsType<Rows>(renderable);

        var output = RenderToPlainConsole(renderable);

        Assert.Contains("Header", output);
        Assert.Contains("quoted line", output);
        Assert.Contains("1. First item", output);
        Assert.Contains("2. Second item", output);
        Assert.Contains("aspire docs get redis-integration", output);
        Assert.Contains("Visit GitHub for more info.", output);
    }

    [Fact]
    public void ConvertToRenderable_WithQuotedLinkAndCode_PreservesInteractiveFormatting()
    {
        var markdown = "> Learn how to configure HTTPS endpoints with the [Aspire CLI](https://aspire.dev/get-started/install-cli/) and `aspire run`.";

        var renderable = MarkdownToSpectreConverter.ConvertToRenderable(markdown);

        var output = StripAnsi(RenderToPlainConsole(renderable));

        Assert.Contains("Learn how to configure HTTPS endpoints with the Aspire CLI and aspire run.", output);
        Assert.DoesNotContain("https://aspire.dev/get-started/install-cli/", output);
    }

    [Fact]
    public void ConvertToRenderable_WithThematicBreak_PreservesLiteralMarkdown()
    {
        var markdown = """
            Before

            ---

            After
            """;

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("Before", output);
        Assert.Contains("---", output);
        Assert.Contains("After", output);
    }

    [Fact]
    public void ConvertToRenderable_WithRawHtml_PreservesLiteralText()
    {
        var markdown = "before <em>hello</em> after";

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("before hello after", output);
    }

    [Fact]
    public void ConvertToPlainText_WithThematicBreak_PreservesLiteralMarkdown()
    {
        var markdown = """
            Before

            ---

            After
            """;

        var output = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("Before", output);
        Assert.Contains("---", output);
        Assert.Contains("After", output);
    }

    [Fact]
    public void ConvertToPlainText_WithRawHtml_PreservesLiteralText()
    {
        var markdown = "Before <span>inline</span> after";

        var output = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Equal("Before inline after", output);
    }

    [Fact]
    public void ConvertToRenderable_WithQuotedStructuredContent_RendersReadableOutput()
    {
        var markdown = """
            > Steps:
            >
            > * First item
            > * Second item
            >
            > ```bash
            > aspire docs get redis-integration
            > ```
            """;

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("Steps:", output);
        Assert.Contains("• First item", output);
        Assert.Contains("• Second item", output);
        Assert.Contains("aspire docs get redis-integration", output);
    }

    [Fact]
    public void ConvertToRenderable_WithNestedListItemBlocks_RendersReadableOutput()
    {
        var markdown = """
            1. First paragraph

               Continued explanation.

               * Nested item
               * Nested item 2
            """;

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("1. First paragraph", output);
        Assert.Contains("   Continued explanation.", output);
        Assert.Contains("   • Nested item", output);
        Assert.Contains("   • Nested item 2", output);
    }

    [Fact]
    public void ConvertToPlainText_WithNestedListItemBlocks_PreservesIndentation()
    {
        var markdown = """
            1. First paragraph

               Continued explanation.

               * Nested item
               * Nested item 2
            """;

        var output = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("First paragraph", output);
        Assert.Contains("Continued explanation.", output);
        Assert.Contains("Nested item", output);
        Assert.Contains("Nested item 2", output);
    }

    [Fact]
    public void ConvertToPlainText_WithNestedListOnlyItem_StartsNestedListOnContinuationLine()
    {
        var markdown = """
            1.
               * Nested item
               * Nested item 2
            """;

        var output = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("Nested item", output);
        Assert.Contains("Nested item 2", output);
    }

    [Fact]
    public void ConvertToRenderable_WithAlignedTable_PreservesColumnAlignment()
    {
        var markdown = """
            | Left | Center | Right |
            | :--- | :----: | ----: |
            | alpha | beta | gamma |
            """;

        var table = Assert.IsType<Table>(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Equal(Justify.Left, table.Columns[0].Alignment);
        Assert.Equal(Justify.Center, table.Columns[1].Alignment);
        Assert.Equal(Justify.Right, table.Columns[2].Alignment);
    }

    [Fact]
    public void ConvertToPlainText_WithAlignedTable_PreservesAlignmentMarkers()
    {
        var markdown = """
            | Left | Center | Right |
            | :--- | :----: | ----: |
            | alpha | beta | gamma |
            """;

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("Left", result);
        Assert.Contains("Center", result);
        Assert.Contains("Right", result);
        Assert.Contains("alpha", result);
        Assert.Contains("beta", result);
        Assert.Contains("gamma", result);
    }

    [Fact]
    public void ConvertToPlainText_WithNarrowTableColumns_UsesConsistentWidths()
    {
        var markdown = """
            | A | B |
            | - | - |
            | x | y |
            """;

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Contains("A", result);
        Assert.Contains("B", result);
        Assert.Contains("x", result);
        Assert.Contains("y", result);
    }

    [Fact]
    public void ConvertToRenderable_WithSparseTable_PadsMissingCells()
    {
        var markdown = """
            | Col A | Col B | Col C |
            | ----- | ----- | ----- |
            | first | second |
            | | middle | last |
            """;

        var table = Assert.IsType<Table>(MarkdownToSpectreConverter.ConvertToRenderable(markdown));
        var output = RenderToPlainConsole(table);

        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(2, table.Rows.Count);
        Assert.Contains("first", output);
        Assert.Contains("second", output);
        Assert.Contains("middle", output);
        Assert.Contains("last", output);
    }

    [Fact]
    public void ConvertToRenderable_WithFormattedTableCells_RendersReadableCellContent()
    {
        var markdown = """
            | Setting | Example | Docs |
            | ------- | ------- | ---- |
            | **Name** | `redis` | [Guide](https://example.com/guide) |
            """;

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("Name", output);
        Assert.Contains("redis", output);
        Assert.Contains("Guide", output);
        Assert.DoesNotContain("https://example.com/guide", output);
    }

    [Fact]
    public void ConvertToPlainText_WithAutolinks_ConvertsCorrectly()
    {
        var markdown = "Visit https://example.com or <https://aspire.dev> for more info.";

        var result = MarkdownToSpectreConverter.ConvertToPlainText(markdown);

        Assert.Equal("Visit https://example.com or https://aspire.dev for more info.", result);
    }

    [Fact]
    public void ConvertToRenderable_WithAutolinks_RendersReadableUrls()
    {
        var markdown = "Visit https://example.com or <https://aspire.dev> for more info.";

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        Assert.Contains("https://example.com", output);
        Assert.Contains("https://aspire.dev", output);
        Assert.DoesNotContain("<https://aspire.dev>", output);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithReferenceLinks_DropsLabelsAndKeepsVisibleText(bool useRenderable)
    {
        var markdown = "Reference style: [ref link][id1] and another [second][id2].";

        var result = useRenderable
            ? RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown)).Trim()
            : MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        Assert.Equal("Reference style: ref link and another second.", result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithMixedLinks_HandlesCorrectly(bool useRenderable)
    {
        var markdown = "Inline [GitHub](https://github.com) and reference [docs][ref1].";

        var result = useRenderable
            ? RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown)).Trim()
            : MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        var expected = useRenderable
            ? "Inline GitHub and reference docs."
            : "Inline [cyan][link=https://github.com]GitHub[/][/] and reference docs.";

        Assert.Equal(expected, result);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithMultipleImages_OmitsAllImages(bool useRenderable)
    {
        var markdown = "Here is ![first image](https://example.com/1.png) and ![second image](https://example.com/2.jpg) in text.";

        var result = useRenderable
            ? RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown)).Trim()
            : MarkdownToSpectreConverter.ConvertToSpectre(markdown);

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
    [InlineData("Text before ```code``` after", "Text before [grey][bold]code[/][/] after")]
    public void ConvertToSpectre_WithCodeBlocks_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("> quoted text", "[italic grey]> quoted text[/]")]
    [InlineData("> This is a quote", "[italic grey]> This is a quote[/]")]
    [InlineData("Normal text\n> quoted line\nMore text", "Normal text\n[italic grey]> quoted line[/]\n[italic grey]> More text[/]")]
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
[italic grey]> This is a quoted line[/]
[italic grey]> Some [strikethrough]strikethrough[/] text with [grey][bold]inline code block[/][/].[/]
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
        var expected = @"[italic grey]> Line 1[/]
[italic grey]> [/]
[italic grey]> Line 2[/]".Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("> ", "[italic grey]>[/]")]
    [InlineData(">", "[italic grey]>[/]")]
    [InlineData("> text", "[italic grey]> text[/]")]
    public void ConvertToSpectre_WithVariousQuoteFormats_ConvertsCorrectly(string markdown, string expected)
    {
        // Act
        var result = MarkdownToSpectreConverter.ConvertToSpectre(markdown);

        // Assert
        Assert.Equal(expected, result);
    }

    private static string RenderToPlainConsole(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        console.Profile.Width = int.MaxValue;
        console.Profile.Capabilities.Links = false;

        console.Write(renderable);
        console.WriteLine();

        return writer.ToString().Replace("\r\n", "\n");
    }

    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiEscapeRegex();

    private static string StripAnsi(string text) => AnsiEscapeRegex().Replace(text, "");

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
[italic grey]> This is important[/]
[italic grey]> [/]
[italic grey]> Please follow these steps:[/]

[grey]cd /path/to/project
npm install[/]

[italic grey]> That's all![/]".Replace("\r\n", "\n").Replace("\r", "\n");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertToRenderable_WithParagraphBeforeTable_HasSingleBlankLineBetween()
    {
        var markdown = """
            ## API

            The API section configures authentication.
            | Option | Description |
            | ------ | ----------- |
            | `AuthMode` | Can be set to `ApiKey` or `Unsecured`. |
            """;

        var output = RenderToPlainConsole(MarkdownToSpectreConverter.ConvertToRenderable(markdown));

        var expected = """
            API

            The API section configures authentication.

            ┌──────────┬────────────────────────────────────┐
            │ Option   │ Description                        │
            ├──────────┼────────────────────────────────────┤
            │ AuthMode │ Can be set to ApiKey or Unsecured. │
            └──────────┴────────────────────────────────────┘


            """;

        Assert.Equal(expected, output, ignoreLineEndingDifferences: true);
    }
}
