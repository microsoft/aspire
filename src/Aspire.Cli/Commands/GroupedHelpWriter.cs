// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Writes grouped help output for the root command, organizing subcommands into logical categories.
/// Groups are determined by each command's <see cref="BaseCommand.HelpGroup"/> property.
/// </summary>
internal static class GroupedHelpWriter
{
    /// <summary>
    /// The well-known group ordering. Groups not listed here appear after these, in alphabetical order.
    /// </summary>
    private static readonly HelpGroup[] s_groupOrder =
    [
        HelpGroup.AppCommands,
        HelpGroup.ResourceManagement,
        HelpGroup.Monitoring,
        HelpGroup.Deployment,
        HelpGroup.ToolsAndConfiguration,
    ];

    /// <summary>
    /// Writes grouped help output for the given root command.
    /// </summary>
    /// <param name="command">The root command to generate help for.</param>
    /// <param name="writer">The text writer to write help output to.</param>
    /// <param name="maxWidth">The maximum console width. When null, defaults to 80.</param>
    public static void WriteHelp(Command command, TextWriter writer, int? maxWidth = null)
    {
        var width = maxWidth ?? 80;

        // Description
        if (!string.IsNullOrEmpty(command.Description))
        {
            writer.WriteLine(command.Description);
            writer.WriteLine();
        }

        // Usage
        writer.WriteLine(HelpGroupStrings.Usage);
        writer.WriteLine(GetIndent() + HelpGroupStrings.UsageSyntax);
        writer.WriteLine();

        // Collect visible subcommands and organize by group.
        var grouped = new Dictionary<HelpGroup, List<BaseCommand>>();
        var ungroupedCommands = new List<Command>();

        foreach (var sub in command.Subcommands)
        {
            if (sub.Hidden)
            {
                continue;
            }

            if (sub is BaseCommand baseCmd && baseCmd.HelpGroup is not HelpGroup.None)
            {
                if (!grouped.TryGetValue(baseCmd.HelpGroup, out var list))
                {
                    list = [];
                    grouped[baseCmd.HelpGroup] = list;
                }

                list.Add(baseCmd);
            }
            else
            {
                ungroupedCommands.Add(sub);
            }
        }

        // Sort commands within each group by name.
        foreach (var list in grouped.Values)
        {
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        // Compute the first-column width across all commands for consistent alignment.
        var columnWidth = 0;
        foreach (var list in grouped.Values)
        {
            foreach (var cmd in list)
            {
                var label = FormatCommandLabel(cmd);
                if (label.Length > columnWidth)
                {
                    columnWidth = label.Length;
                }
            }
        }

        foreach (var cmd in ungroupedCommands)
        {
            var label = FormatCommandLabel(cmd);
            if (label.Length > columnWidth)
            {
                columnWidth = label.Length;
            }
        }

        // Padding: 2 spaces indent + label + at least 2 spaces gap before description
        columnWidth += 4;

        // Write groups in the defined order, then any additional groups alphabetically.
        var writtenGroups = new HashSet<HelpGroup>();

        foreach (var group in s_groupOrder)
        {
            if (grouped.TryGetValue(group, out var commands))
            {
                WriteGroup(writer, GetGroupHeading(group), commands, columnWidth, width);
                writtenGroups.Add(group);
            }
        }

        // Write any groups not in the well-known order (future-proofing).
        foreach (var (group, commands) in grouped.OrderBy(kvp => kvp.Key))
        {
            if (!writtenGroups.Contains(group))
            {
                WriteGroup(writer, GetGroupHeading(group), commands, columnWidth, width);
            }
        }

        // Catch-all: show any registered commands not assigned to a group.
        if (ungroupedCommands.Count > 0)
        {
            writer.WriteLine(HelpGroupStrings.OtherCommands);
            foreach (var cmd in ungroupedCommands.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var label = FormatCommandLabel(cmd);
                var description = cmd.Description ?? string.Empty;
                WriteTwoColumnRow(writer, label, description, columnWidth, width);
            }
            writer.WriteLine();
        }

        // Options
        var visibleOptions = command.Options.Where(o => !o.Hidden).ToList();
        if (visibleOptions.Count > 0)
        {
            writer.WriteLine(HelpGroupStrings.Options);

            var optionColumnWidth = 0;
            foreach (var opt in visibleOptions)
            {
                var label = FormatOptionLabel(opt);
                if (label.Length > optionColumnWidth)
                {
                    optionColumnWidth = label.Length;
                }
            }

            optionColumnWidth += 4;

            foreach (var opt in visibleOptions)
            {
                var label = FormatOptionLabel(opt);
                var desc = opt.Description ?? string.Empty;
                WriteTwoColumnRow(writer, label, desc, optionColumnWidth, width);
            }

            writer.WriteLine();
        }

        // Help hint
        writer.WriteLine(HelpGroupStrings.HelpHint);
    }

    private static void WriteGroup(TextWriter writer, string heading, List<BaseCommand> commands, int columnWidth, int width)
    {
        writer.WriteLine(heading);
        foreach (var cmd in commands)
        {
            var label = FormatCommandLabel(cmd);
            var description = cmd.Description ?? string.Empty;
            WriteTwoColumnRow(writer, label, description, columnWidth, width);
        }
        writer.WriteLine();
    }

    /// <summary>
    /// Gets the localized heading string for a help group.
    /// </summary>
    internal static string GetGroupHeading(HelpGroup group) => group switch
    {
        HelpGroup.AppCommands => HelpGroupStrings.AppCommands,
        HelpGroup.ResourceManagement => HelpGroupStrings.ResourceManagement,
        HelpGroup.Monitoring => HelpGroupStrings.Monitoring,
        HelpGroup.Deployment => HelpGroupStrings.Deployment,
        HelpGroup.ToolsAndConfiguration => HelpGroupStrings.ToolsAndConfiguration,
        _ => group.ToString(),
    };

    /// <summary>
    /// Writes a two-column row with word-wrapping on the description column.
    /// Continuation lines are indented to align with the description start.
    /// </summary>
    private const int IndentWidth = 2;

    private static string GetIndent(int extra = 0) => new(' ', IndentWidth + extra);

    private static void WriteTwoColumnRow(TextWriter writer, string label, string description, int columnWidth, int maxWidth)
    {
        var paddedLabel = label.PadRight(columnWidth);
        var descriptionWidth = maxWidth - columnWidth - IndentWidth;

        // If the terminal is too narrow to wrap meaningfully, just write it all on one line.
        if (descriptionWidth < 20)
        {
            writer.WriteLine($"{GetIndent()}{paddedLabel}{description}");
            return;
        }

        var remaining = description.AsSpan();
        var firstLine = true;

        while (remaining.Length > 0)
        {
            if (firstLine)
            {
                writer.Write(GetIndent());
                writer.Write(paddedLabel);
                firstLine = false;
            }
            else
            {
                // Continuation line: indent to align with description column.
                writer.Write(GetIndent(columnWidth));
            }

            if (remaining.Length <= descriptionWidth)
            {
                writer.WriteLine(remaining);
                break;
            }

            // Find the last space within the allowed width for a clean word break.
            var breakAt = remaining[..descriptionWidth].LastIndexOf(' ');
            if (breakAt <= 0)
            {
                // No space found — hard break at the width limit.
                breakAt = descriptionWidth;
            }

            writer.WriteLine(remaining[..breakAt]);
            remaining = remaining[breakAt..].TrimStart();
        }
    }

    private static string FormatCommandLabel(Command cmd)
    {
        var args = GetArgumentSyntax(cmd);
        return string.IsNullOrEmpty(args) ? cmd.Name : $"{cmd.Name} {args}";
    }

    private static string GetArgumentSyntax(Command cmd)
    {
        if (cmd.Arguments.Count == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var arg in cmd.Arguments)
        {
            if (arg.Hidden)
            {
                continue;
            }

            var name = $"<{arg.Name}>";

            // Optional if minimum arity is 0.
            if (arg.Arity.MinimumNumberOfValues == 0)
            {
                name = $"[{name}]";
            }

            parts.Add(name);
        }

        return string.Join(" ", parts);
    }

    private static string FormatOptionLabel(Option option)
    {
        // Collect all identifiers: Name may not be in Aliases in System.CommandLine 2.0.
        var allNames = new HashSet<string>(option.Aliases, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(option.Name))
        {
            allNames.Add(option.Name);
        }

        var sorted = allNames.OrderBy(a => a.Length).ToList();
        return sorted.Count > 1
            ? $"{sorted[0]}, {sorted[1]}"
            : sorted.Count > 0 ? sorted[0] : option.Name;
    }
}
