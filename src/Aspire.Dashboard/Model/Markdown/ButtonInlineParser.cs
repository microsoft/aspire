// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Syntax.Inlines;

namespace Aspire.Dashboard.Model.Markdown;

/// <summary>
/// Inline parser for button syntax: [Text](type=button action=value arguments=value icon=value)
///
/// Triggered when the link URL starts with "type=button". Uses space-delimited key=value pairs
/// inside parentheses. The first '=' in each pair separates key from value, so argument values
/// (query strings with '=' and '&amp;') work without encoding.
/// </summary>
public sealed class ButtonInlineParser : InlineParser
{
    public ButtonInlineParser()
    {
        OpeningCharacters = ['['];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var start = slice.Start;
        var text = slice.Text;

        // We're at '['. Find the closing ']'.
        var closeBracket = text.IndexOf(']', start + 1);
        if (closeBracket == -1)
        {
            return false;
        }

        // The character after ']' must be '('.
        if (closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return false;
        }

        var openParen = closeBracket + 1;

        // Find the closing ')'. Since argument values use query string format (no unescaped parens),
        // we can search for the first ')' after '('.
        var closeParen = text.IndexOf(')', openParen + 1);
        if (closeParen == -1)
        {
            return false;
        }

        // Extract the content inside parentheses and check it starts with "type=button".
        var parenContent = text.Substring(openParen + 1, closeParen - openParen - 1);
        if (!parenContent.StartsWith("type=button", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Extract the button text from [Text]. Text is optional for icon-only buttons.
        var buttonText = text.Substring(start + 1, closeBracket - start - 1);

        // Parse the key=value pairs (skipping "type=button").
        var buttonConfig = ButtonConfig.ParseInline(parenContent);
        buttonConfig.Text = buttonText;

        // At least text or icon must be specified.
        if (string.IsNullOrEmpty(buttonConfig.Text) && string.IsNullOrEmpty(buttonConfig.Icon))
        {
            return false;
        }

        // Create the button inline element
        processor.Inline = new ButtonInline(buttonConfig);

        // Advance the slice past the entire [Text](...) syntax.
        slice.Start = closeParen + 1;
        return true;
    }
}

/// <summary>
/// Custom inline element for button rendering.
/// </summary>
public class ButtonInline : Inline
{
    public ButtonConfig Config { get; }

    public ButtonInline(ButtonConfig config)
    {
        Config = config;
    }
}
