// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// Immutable menu commands retain exact process identities across discovery updates.
/// </summary>
internal sealed record LinuxTrayMenuItem(string Label, LinuxTrayAction Action = LinuxTrayAction.None,
    AppHostMenuItem? Host = null, bool Enabled = true, IReadOnlyList<LinuxTrayMenuItem>? Children = null)
{
    internal LinuxTrayIcon Icon { get; init; }
}

internal enum LinuxTrayIcon
{
    None, Healthy, Warning, Unhealthy, Unknown, Stopped, Documentation, Settings
}

internal enum LinuxTrayAction
{
    None, Separator, Dashboard, Stop, Start, TogglePin, Folder, CopyPath, Code, ClearRecent, Settings, Documentation, Quit
}

internal static class LinuxTrayMenu
{
    internal static IReadOnlyList<LinuxTrayMenuItem> Build(TrayViewState state, bool confirmStop, bool codeAvailable)
    {
        List<LinuxTrayMenuItem> items = [];
        if (state.ShowStatus)
        {
            items.Add(new(state.Status, Enabled: false));
        }
        items.AddRange(state.AppHosts.Select(Host));
        items.Add(new("", LinuxTrayAction.Separator));
        var recent = state.RecentAppHosts.Select(Host).ToList();
        if (recent.Count == 0)
        {
            recent.Add(new("No recently opened AppHosts", Enabled: false));
        }
        recent.Add(new("", LinuxTrayAction.Separator));
        recent.Add(new("Clear recently opened...", LinuxTrayAction.ClearRecent, Enabled: state.CanClearRecent));
        items.Add(new("Recently opened", Children: recent));
        items.Add(new("", LinuxTrayAction.Separator));
        items.Add(new("Documentation", LinuxTrayAction.Documentation) { Icon = LinuxTrayIcon.Documentation });
        items.Add(new("Settings...", LinuxTrayAction.Settings) { Icon = LinuxTrayIcon.Settings });
        items.Add(new("", LinuxTrayAction.Separator));
        items.Add(new("Quit Aspire", LinuxTrayAction.Quit));
        return items;

        LinuxTrayMenuItem Host(AppHostMenuItem host)
        {
            List<LinuxTrayMenuItem> actions = [];
            if (host.IsRunning)
            {
                actions.Add(new("Open dashboard", LinuxTrayAction.Dashboard, host, host.CanOpenDashboard));
                actions.Add(new(host.IsStopping ? "Stopping..." : confirmStop ? "Stop AppHost..." : "Stop AppHost",
                    LinuxTrayAction.Stop, host, host.CanStop));
            }
            else
            {
                actions.Add(new(host.IsStarting ? "Starting..." : "Start AppHost", LinuxTrayAction.Start, host, host.CanStart));
            }
            actions.Add(new(host.IsPinned ? "Unpin AppHost" : "Pin AppHost", LinuxTrayAction.TogglePin, host));
            actions.Add(new("", LinuxTrayAction.Separator));
            actions.Add(new("Open folder", LinuxTrayAction.Folder, host));
            actions.Add(new("Copy path", LinuxTrayAction.CopyPath, host));
            if (codeAvailable)
            {
                actions.Add(new("Open in Visual Studio Code", LinuxTrayAction.Code, host));
            }
            actions.Add(new("", LinuxTrayAction.Separator));
            actions.Add(new(host.Error ?? Status(host), Enabled: false));
            actions.Add(new(AppHostPresentation.GetMenuDetailsText(host.Id.AppHostPath, host.Subtitle), Enabled: false));
            // D-Bus menu hosts vary in icon support. Text also makes health available
            // to screen readers and preserves it on hosts that omit menu artwork.
            var status = state.Discovery != DiscoveryState.Live ? "Discovery unavailable"
                : Status(host);
            return new($"{AppHostPresentation.GetCompactMenuLabel(host)} - {status}", Host: host, Children: actions)
            {
                Icon = host switch
                {
                    { IsStarting: true } or { IsStopping: true } => LinuxTrayIcon.Warning,
                    _ when state.Discovery != DiscoveryState.Live => LinuxTrayIcon.Warning,
                    { Error: not null } => LinuxTrayIcon.Unhealthy,
                    { IsRunning: false } => LinuxTrayIcon.Stopped,
                    _ => host.Health switch
                    {
                        AppHostHealth.Healthy => LinuxTrayIcon.Healthy,
                        AppHostHealth.Warning => LinuxTrayIcon.Warning,
                        AppHostHealth.Unhealthy => LinuxTrayIcon.Unhealthy,
                        _ => LinuxTrayIcon.Unknown
                    }
                }
            };
        }
    }

    private static string Status(AppHostMenuItem host)
        => host.IsStopping ? "Stopping" : host.IsStarting ? "Starting" : !host.IsRunning ? "Stopped"
            : host.Health switch
            {
                AppHostHealth.Healthy => "Healthy",
                AppHostHealth.Warning => "Waiting / degraded",
                AppHostHealth.Unhealthy => "Unhealthy",
                _ => "Health unknown"
            };
}
