// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Utils;

/// <summary>
/// Converts basic Markdown syntax to Spectre.Console markup for CLI display.
/// </summary>
internal static partial class MarkdownToSpectreConverter
{
    /// <summary>
    /// Converts markdown text to Spectre.Console markup.
    /// Supports basic markdown elements: headers, bold, italic, links, and inline code.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The converted Spectre.Console markup text.</returns>
    public static string ConvertToSpectre(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return markdown;
        }

        var result = markdown;

        // Normalize line endings to LF to ensure consistent output
        result = result.Replace("\r\n", "\n").Replace("\r", "\n");

        // Process quoted text (> text) - do this first as it's line-based
        result = ConvertQuotedText(result);

        // Process multi-line code blocks (```) - do this before inline code
        result = ConvertCodeBlocks(result);

        // Process headers (# ## ### #### ##### ######)
        result = ConvertHeaders(result);

        // Process bold text (**bold** or __bold__)
        result = ConvertBold(result);

        // Process italic text (*italic* or _italic_)
        result = ConvertItalic(result);

        // Process strikethrough text (~~text~~)
        result = ConvertStrikethrough(result);

        // Process inline code (`code`)
        result = ConvertInlineCode(result);

        // Process images ![alt](url) - remove them as they can't be displayed in CLI
        result = ConvertImages(result);

        // Process links [text](url)
        result = ConvertLinks(result);

        // Escape any remaining square brackets that could be interpreted as Spectre markup
        result = EscapeRemainingSquareBrackets(result);

        return result;
    }

    /// <summary>
    /// Converts markdown links to plain text.
    /// </summary>
    /// <param name="markdown">The markdown text to convert.</param>
    /// <returns>The text with markdown links converted to the plain text format <c>text (url)</c>.</returns>
    public static string ConvertLinksToPlainText(string markdown)
    {
        return LinkRegex().Replace(markdown, "$1 ($2)");
    }

    private static string ConvertHeaders(string text)
    {
        // Convert ###### Header 6 (most specific first)
        text = HeaderLevel6Regex().Replace(text, "[bold]$1[/]");

        // Convert ##### Header 5
        text = HeaderLevel5Regex().Replace(text, "[bold]$1[/]");

        // Convert #### Header 4
        text = HeaderLevel4Regex().Replace(text, "[bold]$1[/]");

        // Convert ### Header 3
        text = HeaderLevel3Regex().Replace(text, "[bold yellow]$1[/]");

        // Convert ## Header 2
        text = HeaderLevel2Regex().Replace(text, "[bold blue]$1[/]");

        // Convert # Header 1
        text = HeaderLevel1Regex().Replace(text, "[bold green]$1[/]");

        return text;
    }

    private static string ConvertBold(string text)
    {
        // Convert **bold** and __bold__
        text = BoldDoubleAsterisksRegex().Replace(text, "[bold]$1[/]");
        text = BoldDoubleUnderscoresRegex().Replace(text, "[bold]$1[/]");

        return text;
    }

    private static string ConvertItalic(string text)
    {
        // Convert *italic* and _italic_ (but not ** or __)
        text = ItalicSingleAsteriskRegex().Replace(text, "[italic]$1[/]");
        text = ItalicSingleUnderscoreRegex().Replace(text, "[italic]$1[/]");

        return text;
    }

    private static string ConvertStrikethrough(string text)
    {
        // Convert ~~strikethrough~~
        return StrikethroughRegex().Replace(text, "[strikethrough]$1[/]");
    }

    private static string ConvertCodeBlocks(string text)
    {
        // Convert multi-line code blocks ```code```
        // Remove language name from the beginning if present
        return CodeBlockRegex().Replace(text, match =>
        {
            var content = match.Groups[1].Value.Trim();

            // Check if the first line contains a language name (no spaces, common language names)
            var lines = content.Split('\n');
            if (lines.Length > 1)
            {
                var firstLine = lines[0].Trim();
                // If first line looks like a language name (single word, common languages)
                if (!string.IsNullOrEmpty(firstLine) && !firstLine.Contains(' ') && IsLikelyLanguageName(firstLine))
                {
                    // Remove the language line and rejoin
                    var codeContent = string.Join('\n', lines.Skip(1));
                    return $"[grey]{codeContent}[/]";
                }
            }

            return $"[grey]{content}[/]";
        });
    }

    private static bool IsLikelyLanguageName(string text)
    {
        // Common language names that would appear at the start of code blocks
        var commonLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bash", "sh", "shell", "cmd", "powershell", "ps1",
            "javascript", "js", "typescript", "ts", "jsx", "tsx",
            "python", "py", "java", "c", "cpp", "csharp", "cs", "vb",
            "html", "css", "scss", "sass", "less", "xml", "yaml", "yml", "json",
            "sql", "php", "ruby", "rb", "go", "rust", "swift", "kotlin",
            "scala", "clojure", "haskell", "perl", "lua", "r", "matlab",
            "dockerfile", "makefile", "ini", "toml", "properties"
        };

        return commonLanguages.Contains(text);
    }

    private static string ConvertQuotedText(string text)
    {
        // Convert > quoted text - handle all forms: "> text", "> ", and ">"
        // Process line by line to avoid regex matching across line boundaries
        var lines = text.Split('\n');
        var regex = new Regex(@"^>\s*(.*)$");

        for (int i = 0; i < lines.Length; i++)
        {
            var match = regex.Match(lines[i]);
            if (match.Success)
            {
                var content = match.Groups[1].Value;
                lines[i] = $"[italic grey]{content}[/]";
            }
        }

        return string.Join('\n', lines);
    }

    private static string ConvertInlineCode(string text)
    {
        // Convert `code`
        return InlineCodeRegex().Replace(text, "[grey][bold]$1[/][/]");
    }

    private static string ConvertImages(string text)
    {
        // Remove image references ![alt](url) as they can't be displayed in CLI
        return ImageRegex().Replace(text, "");
    }

    private static string ConvertLinks(string text)
    {
        // Convert [text](url) to Spectre.Console link format with cyan color
        return LinkRegex().Replace(text, "[cyan][link=$2]$1[/][/]");
    }

    private static string EscapeRemainingSquareBrackets(string text)
    {
        // Escape any remaining square brackets that are not part of Spectre markup
        // We need to preserve Spectre markup tags like [bold], [/], [blue underline], etc.
        // but escape markdown constructs like reference links [text][ref]

        // Use a regex to find standalone square brackets that are not Spectre markup
        // Spectre markup pattern: [word] or [word word] or [/]
        // Reference/other markdown pattern: everything else with square brackets

        // First, temporarily replace all Spectre markup with placeholders
        var spectreMarkups = new List<string>();
        var spectrePattern = @"\[(?:/?(?:bold|italic|grey|blue|green|yellow|cyan|underline|strikethrough)\s?)+\]|\[/\]|\[link=[^\]]+\]|\[cyan\s+link=[^\]]+\]";
        var spectreRegex = new Regex(spectrePattern);

        var textWithPlaceholders = spectreRegex.Replace(text, match =>
        {
            var placeholder = $"__SPECTRE_MARKUP_{spectreMarkups.Count}__";
            spectreMarkups.Add(match.Value);
            return placeholder;
        });

        // Now escape remaining square brackets
        textWithPlaceholders = textWithPlaceholders.Replace("[", "[[").Replace("]", "]]");

        // Restore Spectre markup
        for (int i = 0; i < spectreMarkups.Count; i++)
        {
            textWithPlaceholders = textWithPlaceholders.Replace($"__SPECTRE_MARKUP_{i}__", spectreMarkups[i]);
        }

        return textWithPlaceholders;
    }

    [GeneratedRegex(@"^###### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel6Regex();

    [GeneratedRegex(@"^##### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel5Regex();

    [GeneratedRegex(@"^#### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel4Regex();

    [GeneratedRegex(@"^### (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel3Regex();

    [GeneratedRegex(@"^## (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel2Regex();

    [GeneratedRegex(@"^# (.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLevel1Regex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*")]
    private static partial Regex BoldDoubleAsterisksRegex();

    [GeneratedRegex(@"__([^_]+)__")]
    private static partial Regex BoldDoubleUnderscoresRegex();

    [GeneratedRegex(@"(?<!\*)\*([^*\n]+)\*(?!\*)")]
    private static partial Regex ItalicSingleAsteriskRegex();

    [GeneratedRegex(@"(?<!_)_([^_\n]+)_(?!_)")]
    private static partial Regex ItalicSingleUnderscoreRegex();

    [GeneratedRegex(@"~~([^~]+)~~")]
    private static partial Regex StrikethroughRegex();

    [GeneratedRegex(@"```\s*(.*?)\s*```", RegexOptions.Singleline)]
    private static partial Regex CodeBlockRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)]+)\)")]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")]
    private static partial Regex LinkRegex();
}
