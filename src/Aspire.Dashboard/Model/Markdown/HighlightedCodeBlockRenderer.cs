// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Resources;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model.Markdown;

public class HighlightedCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    private static readonly Icon s_copyIcon = new Icons.Regular.Size16.Copy();
    private static readonly Icon s_checkmarkIcon = new Icons.Regular.Size16.Checkmark();

    private readonly IStringLocalizer<ControlsStrings> _loc;

    public HighlightedCodeBlockRenderer(IStringLocalizer<ControlsStrings> loc)
    {
        _loc = loc;
    }

    protected override void Write(HtmlRenderer renderer, CodeBlock obj)
    {
        // Markdown output can sometimes contain blank lines at the start and end of a code block.
        // Check for blank lines in these places and remove them before rendering to HTML.
        if (obj.Lines.Count > 0)
        {
            // Remove first line if blank line
            if (obj.Lines.Lines[0].Slice.Length == 0)
            {
                obj.Lines.RemoveAt(0);
            }

            // Remove last line if blank line
            if (obj.Lines.Count > 0 && obj.Lines.Lines[obj.Lines.Count - 1].Slice.Length == 0)
            {
                obj.Lines.RemoveAt(obj.Lines.Count - 1);
            }
        }

        // Don't render anything until code block content is available.
        // This means the complete syntax is available when rendering.
        if (obj.Lines.Count == 0 && obj.IsOpen)
        {
            return;
        }

        var codeAttributes = new HtmlAttributes();

        // If there isn't a language specified then default to generic "code"
        string title;
        if (obj is FencedCodeBlock fencedCode && fencedCode.Info is { Length: > 0 } info)
        {
            // Language is added to a CSS class name for highlightjs.
            // Fix known languages that contain invalid CSS class name characters.
            title = info.ToLowerInvariant() switch
            {
                "c#" => "csharp",
                "c++" => "cpp",
                _ => info.ToLowerInvariant()
            };

            codeAttributes.AddClass($"language-{title}");

            // This isn't used by highlightjs but it might be useful to see the underlying value in the HTML.
            codeAttributes.AddProperty("data-language", info);
        }
        else
        {
            title = "code";
        }

        // Add copy attributes to the copy button.
        var rawCode = GetRawCodeText(obj);
        var attributes = FluentUIExtensions.GetClipboardCopyAdditionalAttributes(rawCode, _loc[nameof(ControlsStrings.GridValueCopyToClipboard)], _loc[nameof(ControlsStrings.GridValueCopied)]);
        var copyButtonAttributes = new HtmlAttributes();
        copyButtonAttributes.AddClass("code-copy-button");
        copyButtonAttributes.AddProperty("id", $"code-copy-button-{obj.Span.Start}");
        foreach (var item in attributes)
        {
            copyButtonAttributes.AddProperty(item.Key, item.Value.ToString()!);
        }

        // Render the code block along with surrounding divs for styling, positioning, and the copy button.
        renderer.EnsureLine();
        renderer.Write("<pre>");
        renderer.Writer.Write(@"<div class=""code-block"">");

        renderer.Writer.Write(@"<div class=""code-title"">");
        renderer.Writer.Write(title);
        renderer.Writer.Write("</div>");

        renderer.Writer.Write(@"<div class=""code-buttons-anchor"">");
        renderer.Writer.Write(@"<div class=""code-buttons-hover"">");
        renderer.Writer.Write("<button");
        renderer.WriteAttributes(copyButtonAttributes);
        renderer.Writer.Write('>');
        renderer.Writer.Write(@"<div class=""copy-icon"">");
        renderer.Writer.Write(ToMarkup(s_copyIcon));
        renderer.Writer.Write("</div>");
        renderer.Writer.Write(@"<div class=""checkmark-icon"" style=""display:none;"">");
        renderer.Writer.Write(ToMarkup(s_checkmarkIcon));
        renderer.Writer.Write("</div>");
        renderer.Writer.Write("</button>");
        renderer.Writer.Write("</div>");
        renderer.Writer.Write("</div>");

        renderer.Write(@"<div class=""code-container"">");
        renderer.Write("<code");
        renderer.WriteAttributes(codeAttributes);
        renderer.Writer.Write('>');
        renderer.WriteLeafRawLines(obj, true, true);
        renderer.Writer.Write("</code>");
        renderer.Writer.Write("</div>");

        renderer.Writer.Write("</div>");
        renderer.Writer.Write("</pre>");
        renderer.EnsureLine();
    }

    public static string GetRawCodeText(CodeBlock codeBlock)
    {
        var sb = new StringBuilder();

        var slices = codeBlock.Lines.Lines;
        if (slices is not null)
        {
            for (var i = 0; i < slices.Length; i++)
            {
                ref var slice = ref slices[i].Slice;
                if (slice.Text is null)
                {
                    break;
                }

                sb.Append(slice.AsSpan());
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    public static string ToMarkup(Icon icon)
    {
        var sizePx = (int)icon.Size;
        var size = $"{sizePx}px";
        return $@"<svg viewBox=""0 0 {sizePx} {sizePx}"" width=""{size}"" fill=""var(--accent-fill-rest)"" style=""width: {size};"" aria-hidden=""true"">{icon.Content}</svg>";
    }
}
