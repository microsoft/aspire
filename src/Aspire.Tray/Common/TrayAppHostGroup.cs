// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>A section's application row and its individually actionable instances.</summary>
internal sealed record TrayAppHostGroup(TrayApplicationKey? Key, string Name, IReadOnlyList<AppHostMenuItem> Hosts)
{
    public bool IsGroup => Key is not null && Hosts.Count > 1;

    public static IReadOnlyList<TrayAppHostGroup> Create(IReadOnlyList<AppHostMenuItem> hosts)
    {
        var groups = new List<List<AppHostMenuItem>>();
        var byApplication = new Dictionary<TrayApplicationKey, List<AppHostMenuItem>>();
        foreach (var host in hosts)
        {
            if (host.Checkout is not { } checkout)
            {
                groups.Add([host]);
                continue;
            }
            if (!byApplication.TryGetValue(checkout.Application, out var group))
            {
                group = [];
                byApplication.Add(checkout.Application, group);
                groups.Add(group);
            }
            group.Add(host);
        }
        return groups.Select(group => new TrayAppHostGroup(group[0].Checkout?.Application,
            GetApplicationName(group[0]),
            group.Count > 1 ? LabelInstances(group) : group)).ToArray();
    }

    private static string GetApplicationName(AppHostMenuItem host)
        => host.Checkout is { } checkout
            ? AppHostPresentation.GetDisplayName(new(checkout.CanonicalAppHostPath, 0, null)) : host.DisplayName;

    private static AppHostMenuItem[] LabelInstances(List<AppHostMenuItem> hosts)
    {
        var labels = hosts.Select(host => host.Checkout!.Label).ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        return hosts.Select((host, index) =>
        {
            var label = labels[index];
            // Put disambiguators first so native text ellipsis cannot hide them. Long branch
            // names can otherwise differ only beyond the narrow row's visible text.
            if (label.Length > 24 || labels.Count(other => other == label) > 1)
            {
                label = $"{index + 1}. {label}";
            }
            // A generated label can itself be another branch's literal name.
            for (var attempt = 1; !used.Add(label); attempt++)
            {
                label = $"{index + 1}.{attempt}. {labels[index]}";
            }
            return host with { Title = label, DisplayName = label };
        }).ToArray();
    }

    public PanelStatusGlyph Status(bool live)
    {
        var statuses = Hosts.Select(host => TrayPanel.GetStatusGlyph(host, live)).ToArray();
        // An unknown/stopped child cannot make a partially known application appear healthy.
        foreach (var status in new[] { PanelStatusGlyph.Unhealthy, PanelStatusGlyph.Warning, PanelStatusGlyph.Busy,
            PanelStatusGlyph.Unknown, PanelStatusGlyph.Stopped })
        {
            if (statuses.Contains(status))
            {
                return status;
            }
        }
        return PanelStatusGlyph.Healthy;
    }
}
