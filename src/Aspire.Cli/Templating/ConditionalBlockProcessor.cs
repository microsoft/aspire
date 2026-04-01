// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Aspire.Cli.Templating;

/// <summary>
/// Processes conditional blocks in template content. Blocks are delimited by
/// marker lines of the form <c>{{#name}}</c> / <c>{{/name}}</c>. When a block
/// is included, the marker lines are stripped and the inner content is kept;
/// when excluded, the marker lines and their content are removed entirely.
/// Marker lines may contain leading comment characters (e.g. <c>// {{#name}}</c>
/// or <c># {{#name}}</c>) — the entire line is always removed.
/// </summary>
/// <remarks>
/// Blocks must not overlap or nest across different condition names. Each condition
/// is processed independently in enumeration order. Overlapping blocks produce
/// undefined behavior.
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
            content = ProcessBlock(content, blockName, include);
        }

        Debug.Assert(
            !LeftoverMarkerPattern().IsMatch(content),
            $"Template content contains unprocessed conditional markers. Ensure all block names are included in the conditions dictionary.");

        return content;
    }

    [GeneratedRegex(@"\{\{[#/][a-zA-Z][\w-]*\}\}")]
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
    {
        var startPattern = $"{{{{#{blockName}}}}}";
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
                    $"Template contains opening marker '{{{{#{blockName}}}}}' without a matching closing marker '{{{{/{blockName}}}}}'.");
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
