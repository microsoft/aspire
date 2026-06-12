// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Markdig;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Dashboard.Model.Markdown;

/// <summary>
/// Markdig extension that adds button inline support.
/// Syntax: [Text](type=button action=value arguments=key1=val1&amp;key2=val2 icon=value)
///
/// Uses space-delimited key=value pairs inside parentheses.
/// The first '=' separates key from value, allowing argument values (query strings)
/// to contain '=' without encoding.
/// </summary>
public sealed class ButtonExtension : IMarkdownExtension
{
    private readonly IconResolver _iconResolver;

    public ButtonExtension(IconResolver? iconResolver = null)
    {
        _iconResolver = iconResolver ?? new IconResolver(NullLogger<IconResolver>.Instance);
    }

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.InlineParsers.Contains<ButtonInlineParser>())
        {
            // Insert at the beginning so the button parser runs before the built-in link parser,
            // which also triggers on '['. This ensures [Text](type=button ...) is recognized as a button.
            pipeline.InlineParsers.Insert(0, new ButtonInlineParser());
        }
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
        if (renderer is Markdig.Renderers.HtmlRenderer htmlRenderer)
        {
            if (!htmlRenderer.ObjectRenderers.Contains<ButtonInlineRenderer>())
            {
                htmlRenderer.ObjectRenderers.Add(new ButtonInlineRenderer(_iconResolver));
            }
        }
    }
}
