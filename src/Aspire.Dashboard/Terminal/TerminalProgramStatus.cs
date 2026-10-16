// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;

namespace Aspire.Dashboard.Terminal;

public sealed record TerminalProgramStatus
{
    public int TerminalId { get; init; }
    public int Generation { get; init; }
    public bool HasStatus { get; init; }
    public IReadOnlyList<TerminalProgramStatusRecord> Records { get; init; } = [];
}

public sealed record TerminalProgramStatusRecord
{
    public string Id { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string? Kind { get; init; }
    public int? Progress { get; init; }
    public string? App { get; init; }
    public string? EffectiveApp { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
}

public sealed class TerminalProgramStatusNode(string id)
{
    public string Id { get; } = id;
    public TerminalProgramStatusRecord? Record { get; private set; }
    public List<TerminalProgramStatusNode> Children { get; } = [];

    public static IReadOnlyList<TerminalProgramStatusNode> CreateTree(IReadOnlyList<TerminalProgramStatusRecord> records)
    {
        var nodes = new Dictionary<string, TerminalProgramStatusNode>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            GetNode(record.Id).Record = record;
            // IDs such as "deploy/east/build" may omit either ancestor's record.
            // Group those paths without inventing status or inherited application metadata.
            var parent = record.Id;
            while (parent.LastIndexOf('/') is var slash && slash >= 0)
            {
                parent = parent[..slash];
                GetNode(parent);
            }
        }

        var roots = new List<TerminalProgramStatusNode>();
        foreach (var node in nodes.Values.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            var slash = node.Id.LastIndexOf('/');
            var parentId = slash < 0 ? string.Empty : node.Id[..slash];
            if (node.Id.Length > 0 && nodes.TryGetValue(parentId, out var parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }
        return roots;

        TerminalProgramStatusNode GetNode(string nodeId)
        {
            if (!nodes.TryGetValue(nodeId, out var node))
            {
                nodes.Add(nodeId, node = new TerminalProgramStatusNode(nodeId));
            }
            return node;
        }
    }

    public static string DisplayText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Status is untrusted workload text displayed outside the terminal grid.
        // Disarm invisible formatting, including bidi overrides, without losing Unicode text.
        // https://www.unicode.org/reports/tr9/#Explicit_Directional_Overrides
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                builder.Append(rune);
            }
        }
        return builder.ToString();
    }
}
