// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model.Interaction;

public static class InteractionMarkdownHelper
{
    public static MarkdownProcessor CreateProcessor(IStringLocalizer<ControlsStrings> loc, List<IMarkdownExtension>? extensions = null, bool enableHtml = false)
    {
        // Interaction Markdown comes from the app host so there aren't restrictions on URL schemes.
        return new MarkdownProcessor(loc, safeUrlSchemes: null, extensions: extensions ?? [], disableHtml: !enableHtml);
    }

    public static MarkupString ToMarkupString(MarkdownProcessor markdownProcessor, string markdown)
    {
        return (MarkupString)ToHtml(markdownProcessor, markdown);
    }

    public static string ToHtml(MarkdownProcessor markdownProcessor, string markdown)
    {
        var suppressSurroundingParagraph = MarkdownHelpers.GetSuppressSurroundingParagraph(markdown, suppressParagraphOnNewLines: true);

        return markdownProcessor.ToHtml(markdown, inCompleteDocument: false, suppressSurroundingParagraph: suppressSurroundingParagraph);
    }
}
