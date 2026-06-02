// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Templating;

/// <summary>
/// Processes conditional blocks in template content. Blocks are delimited by
/// marker lines of the form <c>{{#name}}</c> / <c>{{/name}}</c> (positive) or
/// <c>{{^name}}</c> / <c>{{/name}}</c> (inverted, Mustache-style — kept when the
/// condition is <see langword="false"/>). When a block is included, the marker
/// lines are stripped and the inner content is kept; when excluded, the marker
/// lines and their content are removed entirely. Marker lines may contain leading
/// comment characters (e.g. <c>// {{#name}}</c> or <c># {{#name}}</c>) — the
/// entire line is always removed.
/// </summary>
/// <remarks>
/// Blocks must not overlap or nest across different condition names. Each condition
/// is processed independently in enumeration order. Overlapping blocks produce
/// undefined behavior. Positive and inverted blocks for the same condition may
/// appear in any order in the template; each form is processed in a separate pass.
/// </remarks>
internal static partial class ConditionalBlockProcessor
{
    /// <summary>
    /// Processes all conditional blocks for the given set of conditions. Each entry
    /// in <paramref name="conditions"/> maps a block name to whether it should be included.
    /// </summary>
    /// <param name="content">The template content to process.</param>
    /// <param name="conditions">A set of block-name to include/exclude mappings.</param>
    /// <returns>The processed content with conditional blocks resolved.</returns>
    internal static string Process(string content, IReadOnlyDictionary<string, bool> conditions)
    {
        foreach (var (blockName, include) in conditions)
        {
            // Positive section: {{#name}}...{{/name}} — kept when condition is true.
            content = ProcessBlock(content, blockName, startMarkerChar: '#', include);
            // Inverted section: {{^name}}...{{/name}} — kept when condition is false.
            content = ProcessBlock(content, blockName, startMarkerChar: '^', !include);
        }

        // A single Process call is expected to resolve every conditional block, so a
        // leftover marker means the template referenced a condition the caller never
        // supplied. Fail loudly in all build configurations rather than shipping the
        // raw marker to the user.
        EnsureNoConditionalMarkers(content);

        return content;
    }

    /// <summary>
    /// Throws when <paramref name="content"/> still contains a conditional-block
    /// marker (<c>{{#name}}</c>, <c>{{^name}}</c>, or <c>{{/name}}</c>) after all
    /// known conditions have been processed. Unlike the <see cref="Process"/>
    /// debug assertion, this runs in every build configuration so a template that
    /// references an undeclared condition fails loudly instead of shipping the raw
    /// marker to the user. It deliberately matches only the <c>#</c>/<c>^</c>/<c>/</c>
    /// marker forms, so ordinary <c>{{token}}</c> text (e.g. the
    /// <c>{{ApiService_HostAddress}}</c> variables in Visual Studio <c>.http</c>
    /// files) is left untouched.
    /// </summary>
    internal static void EnsureNoConditionalMarkers(string content)
    {
        var match = LeftoverMarkerPattern().Match(content);
        if (match.Success)
        {
            throw new InvalidOperationException(
                $"Template content contains an unprocessed conditional marker '{match.Value}'. Ensure every condition used by the template is declared in its manifest and supplied at render time.");
        }
    }

    [GeneratedRegex(@"\{\{[#/^][a-zA-Z][\w-]*\}\}")]
    private static partial Regex LeftoverMarkerPattern();

    /// <summary>
    /// Processes all occurrences of a single conditional block in the content.
    /// </summary>
    /// <param name="content">The template content to process.</param>
    /// <param name="blockName">The name of the conditional block (e.g. <c>redis</c>).</param>
    /// <param name="include">
    /// When <see langword="true"/>, the block content is kept and only the marker lines
    /// are removed. When <see langword="false"/>, the entire block (markers and content) is removed.
    /// </param>
    /// <returns>The processed content.</returns>
    internal static string ProcessBlock(string content, string blockName, bool include)
        => ProcessBlock(content, blockName, startMarkerChar: '#', include);

    private static string ProcessBlock(string content, string blockName, char startMarkerChar, bool include)
    {
        var startPattern = $"{{{{{startMarkerChar}{blockName}}}}}";
        var endPattern = $"{{{{/{blockName}}}}}";

        while (true)
        {
            var startIdx = content.IndexOf(startPattern, StringComparison.Ordinal);
            if (startIdx < 0)
            {
                break;
            }

            var endIdx = content.IndexOf(endPattern, startIdx, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                throw new InvalidOperationException(
                    $"Template contains opening marker '{{{{{startMarkerChar}{blockName}}}}}' without a matching closing marker '{{{{/{blockName}}}}}'.");
            }

            // Find the full start marker line (including leading whitespace/comments and trailing newline).
            var startLineBegin = content.LastIndexOf('\n', startIdx);
            startLineBegin = startLineBegin < 0 ? 0 : startLineBegin + 1;
            var startLineEnd = content.IndexOf('\n', startIdx);
            startLineEnd = startLineEnd < 0 ? content.Length : startLineEnd + 1;

            // Find the full end marker line.
            var endLineBegin = content.LastIndexOf('\n', endIdx);
            endLineBegin = endLineBegin < 0 ? 0 : endLineBegin + 1;
            var endLineEnd = content.IndexOf('\n', endIdx);
            endLineEnd = endLineEnd < 0 ? content.Length : endLineEnd + 1;

            if (include)
            {
                // Keep the block content but remove the marker lines.
                var blockContent = content[startLineEnd..endLineBegin];
                content = string.Concat(content.AsSpan(0, startLineBegin), blockContent, content.AsSpan(endLineEnd));
            }
            else
            {
                // Remove everything from start marker line to end marker line (inclusive).
                content = string.Concat(content.AsSpan(0, startLineBegin), content.AsSpan(endLineEnd));
            }
        }

        return content;
    }
}
