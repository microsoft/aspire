// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Markdig.Renderers;
using Markdig.Renderers.Html;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model.Markdown;

/// <summary>
/// Renderer for button inline elements that outputs FluentUI button HTML.
/// </summary>
public sealed class ButtonInlineRenderer : HtmlObjectRenderer<ButtonInline>
{
    private readonly IconResolver _iconResolver;

    public ButtonInlineRenderer(IconResolver iconResolver)
    {
        _iconResolver = iconResolver;
    }

    protected override void Write(HtmlRenderer renderer, ButtonInline obj)
    {
        var config = obj.Config;

        if (string.IsNullOrEmpty(config.Text) && string.IsNullOrEmpty(config.Icon))
        {
            return;
        }

        // Use text for display, fall back to icon name for accessibility.
        var label = !string.IsNullOrEmpty(config.Text) ? config.Text : config.Icon!;

        var attributes = new HtmlAttributes();
        attributes.AddProperty("type", "button");
        attributes.AddProperty("title", label);
        attributes.AddProperty("appearance", "neutral");
        attributes.AddClass("neutral");
        attributes.AddProperty("aria-label", label);
        attributes.AddProperty("current-value", "");

        // Emit known config as data attributes so JS can forward them to Blazor.
        attributes.AddProperty("data-text", label);

        if (!string.IsNullOrEmpty(config.Icon))
        {
            attributes.AddProperty("data-icon", config.Icon);
        }

        // Emit additional values as data attributes.
        foreach (var (key, value) in config.Values)
        {
            attributes.AddProperty($"data-{key}", value);
        }

        renderer.Write("<fluent-button");
        renderer.WriteAttributes(attributes);
        renderer.Write('>');

        // Render icon if specified
        if (!string.IsNullOrEmpty(config.Icon))
        {
            var iconSvg = GetIconSvg(config.Icon);
            if (!string.IsNullOrEmpty(iconSvg))
            {
                // Use "start" slot when there's text alongside, no slot for icon-only.
                var slot = string.IsNullOrEmpty(config.Text) ? "" : @" slot=""start""";
                renderer.Write($@"<svg{slot} style=""width: 16px; fill: var(--accent-fill-rest);"" focusable=""false"" viewBox=""0 0 16 16"" aria-hidden=""true"">");
                renderer.Write(iconSvg);
                renderer.Write("</svg>");
            }
        }

        if (!string.IsNullOrEmpty(config.Text))
        {
            renderer.Write(config.Text);
        }
        renderer.Write("</fluent-button>");
    }

    private string? GetIconSvg(string iconName)
    {
        return _iconResolver.ResolveIconName(iconName, IconSize.Size16, IconVariant.Regular)?.Content;
    }
}
