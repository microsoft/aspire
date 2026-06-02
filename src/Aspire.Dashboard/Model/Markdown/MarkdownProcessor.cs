// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Resources;
using Markdig;
using Markdig.Extensions.AutoLinks;
using Markdig.Parsers;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Model.Markdown;

public sealed class MarkdownProcessor
{
    private readonly MarkdownPipeline _markdownPipeline;
    private readonly HashSet<string>? _safeUrlSchemes;

    public MarkdownProcessor(IStringLocalizer<ControlsStrings> loc, HashSet<string>? safeUrlSchemes, List<IMarkdownExtension> extensions, bool disableHtml = true)
    {
        var autoLinkOptions = new AutoLinkOptions
        {
            OpenInNewWindow = true,
            AllowDomainWithoutPeriod = true
        };
        autoLinkOptions.ValidPreviousCharacters += "`";

        var pipelineBuilder = new MarkdownPipelineBuilder();
        pipelineBuilder.ConfigureNewLine(Environment.NewLine);
        if (disableHtml)
        {
            pipelineBuilder.DisableHtml();
        }
        pipelineBuilder.UseAutoLinks(autoLinkOptions);
        pipelineBuilder.UseGridTables();
        pipelineBuilder.UsePipeTables();
        pipelineBuilder.UseEmphasisExtras();
        // Remove IndentedCodeBlockParser because some generators mess up indenting around code blocks.
        // This should be fine because code blocks returned by model should always be fenced.
        pipelineBuilder.BlockParsers.RemoveAll(p => p is IndentedCodeBlockParser);
        pipelineBuilder.Extensions.Add(new AspireCodeBlockExtension(loc));
        foreach (var extension in extensions)
        {
            pipelineBuilder.Extensions.Add(extension);
        }

        _markdownPipeline = pipelineBuilder.Build();
        _safeUrlSchemes = safeUrlSchemes;
    }

    public string ToHtml(string markdown, bool inCompleteDocument = false, bool suppressSurroundingParagraph = false)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        return MarkdownHelpers.ToHtml(markdown, new MarkdownOptions
        {
            Pipeline = _markdownPipeline,
            SuppressSurroundingParagraph = suppressSurroundingParagraph,
            AllowedUrlSchemes = _safeUrlSchemes,
            IncompleteDocument = inCompleteDocument
        });
    }
}
