// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils.Markdown;

namespace Aspire.Cli.Tests.Utils;

public class MarkdownLinkConverterTests
{
    [Fact]
    public void ConvertLinksToPlainText_WithBracketedLinkText_ConvertsCorrectly()
    {
        var result = MarkdownLinkConverter.ConvertLinksToPlainText(
            "[CreateBuilder(string[])](https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)");

        Assert.Equal(
            "CreateBuilder(string[]) (https://aspire.dev/reference/api/csharp/aspire.hosting/distributedapplication/methods.md#createbuilder-string)",
            result);
    }

    [Theory]
    [InlineData("[ Discord](https://aka.ms/aspire-discord)", "Discord (https://aka.ms/aspire-discord)")]
    [InlineData("[Discord ](https://aka.ms/aspire-discord)", "Discord (https://aka.ms/aspire-discord)")]
    [InlineData("[ GitHub ](https://github.com)", "GitHub (https://github.com)")]
    [InlineData("[no-trim](https://example.com)", "no-trim (https://example.com)")]
    public void ConvertLinksToPlainText_TrimsWhitespaceFromLinkText(string markdown, string expected)
    {
        var result = MarkdownLinkConverter.ConvertLinksToPlainText(markdown);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ConvertLinksToPlainText_WithSurroundingText_TrimsWhitespaceFromLinkText()
    {
        var result = MarkdownLinkConverter.ConvertLinksToPlainText(
            "Drop by [ Discord](https://aka.ms/aspire-discord) to chat.");

        Assert.Equal("Drop by Discord (https://aka.ms/aspire-discord) to chat.", result);
    }
}
