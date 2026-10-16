// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Terminal;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

public partial class TerminalProgramStatusTree : ComponentBase
{
    [Parameter, EditorRequired]
    public required IReadOnlyList<TerminalProgramStatusNode> Nodes { get; set; }

    [Parameter, EditorRequired]
    public required HashSet<string> CollapsedIds { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.TerminalStrings> Loc { get; init; }

    private string GetTitle(TerminalProgramStatusNode node)
    {
        var title = TerminalProgramStatusNode.DisplayText(node.Record?.Title);
        return title.Length > 0 ? title : node.Id.Length > 0
            ? node.Id[(node.Id.LastIndexOf('/') + 1)..]
            : Loc[nameof(Resources.TerminalStrings.TerminalStatusRoot)];
    }

    private string GetStateLabel(string state) => Loc[state switch
    {
        "idle" => nameof(Resources.TerminalStrings.TerminalStatusIdle),
        "working" => nameof(Resources.TerminalStrings.TerminalStatusWorking),
        "blocked" => nameof(Resources.TerminalStrings.TerminalStatusBlocked),
        "done" => nameof(Resources.TerminalStrings.TerminalStatusDone),
        "error" => nameof(Resources.TerminalStrings.TerminalStatusError),
        _ => throw new ArgumentException("Unknown terminal program status.", nameof(state))
    }];

    private string GetDescription(TerminalProgramStatusNode node)
    {
        var details = new List<string> { GetTitle(node) };
        if (node.Record is { } record)
        {
            details.Add(GetStateLabel(record.State));
            if (record.Kind is { } kind)
            {
                details.Add(GetKindLabel(kind));
            }
            if (record.Progress is { } progress)
            {
                details.Add(GetProgressLabel(progress));
            }
            if (record.EffectiveApp is { } app)
            {
                details.Add(TerminalProgramStatusNode.DisplayText(app));
            }
            if (record.Message is { Length: > 0 } message)
            {
                details.Add(TerminalProgramStatusNode.DisplayText(message));
            }
        }
        else if (HasGroupingProgress(node))
        {
            details.Add(Loc[nameof(Resources.TerminalStrings.TerminalStatusUnfinishedChildren)]);
        }
        return string.Join(Environment.NewLine, details);
    }

    private static bool HasGroupingProgress(TerminalProgramStatusNode node) =>
        node.Record is null && HasUnfinishedDescendants(node);

    private static bool HasUnfinishedDescendants(TerminalProgramStatusNode node) =>
        node.Children.Any(child => child.Record is { State: not ("done" or "error") } || HasUnfinishedDescendants(child));

    private static Icon GetStatusIcon(string? state) => state switch
    {
        "done" => new Icons.Filled.Size16.CheckmarkCircle(),
        "error" => new Icons.Filled.Size16.ErrorCircle(),
        "blocked" => new Icons.Filled.Size16.Warning(),
        _ => new Icons.Regular.Size16.Circle()
    };

    private static Color GetStatusColor(string? state) => state switch
    {
        "done" => Color.Success,
        "error" => Color.Error,
        "blocked" => Color.Warning,
        _ => Color.Default
    };

    private string GetKindLabel(string kind) => Loc[kind switch
    {
        "permission" => nameof(Resources.TerminalStrings.TerminalStatusPermission),
        "question" => nameof(Resources.TerminalStrings.TerminalStatusQuestion),
        "auth" => nameof(Resources.TerminalStrings.TerminalStatusAuth),
        _ => throw new ArgumentException("Unknown terminal program status kind.", nameof(kind))
    }];

    private static string GetProgressLabel(int progress) => progress.ToString("0", CultureInfo.CurrentCulture) + "%";

    private void ToggleExpanded(TerminalProgramStatusNode node)
    {
        if (node.Children.Count > 0)
        {
            SetExpanded(node.Id, CollapsedIds.Contains(node.Id));
        }
    }

    private void SetExpanded(string id, bool expanded)
    {
        if (expanded)
        {
            CollapsedIds.Remove(id);
        }
        else
        {
            CollapsedIds.Add(id);
        }
    }
}
