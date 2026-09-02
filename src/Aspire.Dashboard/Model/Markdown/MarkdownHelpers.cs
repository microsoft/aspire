// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Aspire.Dashboard.Model.Markdown;

public static class MarkdownHelpers
{
    // These URL schemes are considered always safe to be included in Markdown received from untrusted 3rd parties, e.g. GenAI.
    // Untrusted 3rd party Markdown should be limited to these URLs to avoid links that could trigger other programs that listen for the scheme.
    public static readonly HashSet<string> SafeUrlSchemes = new HashSet<string>(StringComparers.EndpointAnnotationUriScheme) { "http", "https", "mailto" };

    public static string ToHtml(string markdown, MarkdownOptions options)
    {
        var document = Markdig.Markdown.Parse(markdown, options.Pipeline);

        if (options.IncompleteDocument)
        {
            // Don't render partially complete inline code blocks because they can be transformed when rendered to HTML.
            CompletePartialElements(document, renderPartialInlineCode: false);
        }

        // Open absolute links in the response in a new window.
        foreach (var link in document.Descendants<LinkInline>())
        {
            switch (DetectLink(link.Url, options.AllowedUrlSchemes))
            {
                case LinkType.Absolute:
                    AddLinkAttributes(link);
                    break;
                case LinkType.Prohibited:
                    link.Url = string.Empty;
                    break;
            }
        }
        foreach (var link in document.Descendants<AutolinkInline>())
        {
            switch (DetectLink(link.Url, options.AllowedUrlSchemes))
            {
                case LinkType.Absolute:
                    AddLinkAttributes(link);
                    break;
                case LinkType.Prohibited:
                    link.Url = string.Empty;
                    break;
            }
        }

        // Adjust heading levels so that they are appropriate for embedding in a page.
        // Markdown can easily contain h2/h3 headings, which would be greater than the dialog title header.
        // This change ensures all headings are h4 or smaller.
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            heading.Level = heading.Level switch
            {
                1 => 4,
                2 => 4,
                3 => 5,
                4 => 5,
                5 => 6,
                _ => 6
            };
        }

        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        if (options.SuppressSurroundingParagraph)
        {
            // markdig won't render a surrounding paragraph if HtmlRenderer.ImplicitParagraph is true.
            // The naming is odd, but I think the idea is we're telling the renderer that there is an implicit paragraph
            // around the content and so renderer doesn't need to add one.
            renderer.ImplicitParagraph = true;
        }

        options.Pipeline.Setup(renderer);

        renderer.Render(document); // using the renderer directly
        writer.Flush();

        return writer.ToString();

        static LinkType DetectLink(string? url, HashSet<string>? allowedUrlSchemes)
        {
            if (url is null)
            {
                return LinkType.None;
            }

            // Browsers and protocol handlers can accept URLs rejected by System.Uri, so don't render an unclassified URL.
            if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri))
            {
                return LinkType.Prohibited;
            }

            if (!uri.IsAbsoluteUri)
            {
                return LinkType.Relative;
            }

            if (allowedUrlSchemes != null)
            {
                if (!allowedUrlSchemes.Contains(uri.Scheme))
                {
                    return LinkType.Prohibited;
                }
            }
            else
            {
                // Even if no allowed schemes are specified, always block "javascript:" links.
                if (string.Equals(uri.Scheme, "javascript", StringComparison.OrdinalIgnoreCase))
                {
                    return LinkType.Prohibited;
                }
            }

            return LinkType.Absolute;
        }

        static void AddLinkAttributes(IMarkdownObject link)
        {
            var attributes = link.GetAttributes();

            attributes.AddPropertyIfNotExist("target", "_blank");
            attributes.AddPropertyIfNotExist("rel", "noopener noreferrer nofollow");
        }
    }

    public static bool GetSuppressSurroundingParagraph(string markdown, bool suppressParagraphOnNewLines)
    {
        // Avoid adding paragraphs to HTML output from Markdown content unless there are multiple lines (aka multiple paragraphs).
        return suppressParagraphOnNewLines && !(markdown.Contains('\n') || markdown.Contains('\r'));
    }

    private enum LinkType
    {
        None,
        Relative,
        Absolute,
        Prohibited
    }

    public static void CompletePartialElements(MarkdownDocument document, bool renderPartialInlineCode)
    {
        var lastChild = document.LastChild;
        while (lastChild is ContainerBlock containerBlock)
        {
            lastChild = containerBlock.LastChild;
        }

        if (lastChild is not LeafBlock leafBlock)
        {
            return;
        }

        // "Level: 2" means '-' was used.
        if (leafBlock is HeadingBlock { IsSetext: true, Level: 2, HeaderCharCount: 1 } setext)
        {
            var paragraph = new ParagraphBlock();
            paragraph.Inline = new ContainerInline();
            setext.Inline?.EmbraceChildrenBy(paragraph.Inline);

            var parent = setext.Parent!;
            parent[parent.IndexOf(setext)] = paragraph;

            leafBlock = paragraph;
        }

        if (leafBlock.Inline?.LastChild is LiteralInline literal)
        {
            // A LiteralInline with a backtick character is a potential CodeInline that wasn't closed.
            var indexOfBacktick = literal.Content.IndexOf('`');
            if (indexOfBacktick >= 0)
            {
                // But it could also happen if the backticks were escaped.
                if (literal.Content.AsSpan().Count('`') == 1 && !literal.IsFirstCharacterEscaped)
                {
                    // "Text with `a code inline" => "Text with `a code inline`"
                    var originalLength = literal.Content.Length;

                    // Shorten the existing text. -1 to exclude the backtick.
                    literal.Content.End = indexOfBacktick - 1;

                    // Insert a CodeInline with the remainder. +1 and -1 to account for the backtick.
                    if (renderPartialInlineCode)
                    {
                        var code = literal.Content.Text.Substring(indexOfBacktick + 1, originalLength - literal.Content.Length - 1);
                        literal.InsertAfter(new CodeInline(code));
                    }

                    return;
                }
            }

            var previousSibling = literal.PreviousSibling;

            // Handle unclosed bold/italic that don't yet have any following content.
            if (previousSibling is null && IsEmphasisStart(literal.Content.AsSpan()))
            {
                literal.Remove();
                return;
            }

            if (previousSibling is EmphasisInline)
            {
                // Handle cases like "**_foo_ and bar" by skipping the _foo_ emphasis.
                previousSibling = previousSibling.PreviousSibling;
            }

            if (previousSibling is LiteralInline previousInline)
            {
                var content = previousInline.Content.AsSpan();

                // Unclosed bold/italic (EmphasisInline)?
                // Note that this doesn't catch cases with mixed opening chars, e.g. "**_text"
                if (IsEmphasisStart(content))
                {
                    literal.Remove();

                    var emphasis = new EmphasisInline();
                    emphasis.DelimiterChar = '*';
                    emphasis.DelimiterCount = previousInline.Content.Length;

                    previousInline.ReplaceBy(emphasis);

                    if (emphasis.DelimiterCount <= 2)
                    {
                        // Just * or **
                        emphasis.AppendChild(literal);
                    }
                    else
                    {
                        // E.g. "***text", which we need to turn into nested <em><strong>text</strong></em>
                        emphasis.DelimiterCount = 1;

                        var nestedStrong = new EmphasisInline();
                        nestedStrong.DelimiterChar = emphasis.DelimiterChar;
                        nestedStrong.DelimiterCount = 2;

                        nestedStrong.AppendChild(literal);
                        emphasis.AppendChild(nestedStrong);
                    }

                    if (emphasis.NextSibling is EmphasisInline nextSibling)
                    {
                        // This is the EmphasisInline we've skipped before. Fix the ordering.
                        // "**_foo_ and bar" is currently "** and bar**_foo_".
                        // Move the skipped emphasis to be the first child of the node we've generated.
                        nextSibling.Remove();
                        emphasis.FirstChild!.InsertBefore(nextSibling);
                    }

                    return;
                }
                else if (content is "[" or "![")
                {
                    // In-progress link, e.g. [text](http://
                    literal.Remove();
                    previousInline.Remove();
                }
            }
        }
        else if (leafBlock.Inline?.LastChild is LinkDelimiterInline linkDelimiterInline)
        {
            // In-progress link, e.g. [text, or [text]
            linkDelimiterInline.Remove();
        }
        else if (leafBlock.Inline?.LastChild is LinkInline linkInline)
        {
            // In-progress link, e.g. [text](http://
            if (!linkInline.IsClosed)
            {
                linkInline.Remove();
            }
        }

        static bool IsEmphasisStart(ReadOnlySpan<char> text)
        {
            return text is "*" or "**" or "***" or "_" or "__" or "___";
        }
    }
}
