// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Model.Markdown;
using Markdig.Renderers.Normalize;
using Markdig.Syntax;

namespace Aspire.Dashboard.Model.Assistant;

public sealed class FollowUpPromptViewModel
{
    public required string Text { get; init; }
    public required string Html { get; init; }

    public static bool ParseResponseText(MarkdownProcessor markdownProcessor, List<FollowUpPromptViewModel> inProgressFollowUpPrompts, string responseText, bool inProgress)
    {
        var document = Markdig.Markdown.Parse(responseText);

        // Use items with complete content or use all if response is no longer in progress.
        var completedItems = document.Descendants<ListItemBlock>()
            .Where(i => !inProgress || (i.LastChild is { } item && !item.IsOpen))
            .ToList();

        var questions = new List<string>();
        foreach (var item in completedItems)
        {
            if (TryGetQuestionText(item, out var text))
            {
                questions.Add(text);
            }
        }

        // Only add new follow-up prompts.
        var followUpPromptAdded = false;
        for (var i = inProgressFollowUpPrompts.Count; i < completedItems.Count; i++)
        {
            var questionText = questions[i];
            inProgressFollowUpPrompts.Add(new FollowUpPromptViewModel
            {
                Text = questionText,
                Html = markdownProcessor.ToHtml(questionText, suppressSurroundingParagraph: true)
            });
            followUpPromptAdded = true;
        }

        return followUpPromptAdded;
    }

    /// <summary>
    /// Get the list item's markdown as a string.
    /// </summary>
    private static bool TryGetQuestionText(ListItemBlock listItem, [NotNullWhen(true)] out string? text)
    {
        var paragraph = listItem.Descendants<ParagraphBlock>().FirstOrDefault();
        if (paragraph == null)
        {
            text = null;
            return false;
        }

        listItem.Remove(paragraph);

        var markdownDocument = new MarkdownDocument
        {
            paragraph
        };

        var writer = new StringWriter();
        var renderer = new NormalizeRenderer(writer);
        renderer.Render(markdownDocument);
        writer.Flush();

        text = writer.ToString();

        // Trim bold stars if the question was surrounded with them.
        if (text.Length >= 4 && text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal))
        {
            text = text[2..^2];
        }

        // Trim double quotes if the question was surrounded with them.
        if (text.Length >= 2 && text.StartsWith('"') && text.EndsWith('"'))
        {
            text = text[1..^1];
        }

        return true;
    }
}
