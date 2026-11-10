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
    internal LinuxFolderApplication? Application { get; init; }
}

internal enum LinuxTrayIcon
{
    None, Healthy, Warning, Unhealthy, Unknown, Stopped, Documentation, Settings
}

internal enum LinuxTrayAction
{
    None, Separator, Dashboard, Stop, Start, TogglePin, Folder, CopyPath, OpenIn, RemoveRecent, ClearRecent, Settings, Documentation, Quit
}

internal static class LinuxTrayMenu
{
    internal static IReadOnlyList<LinuxTrayMenuItem> Build(TrayViewState state, IReadOnlyList<LinuxFolderApplication> applications)
    {
        var (summary, _) = TrayPanel.GetHeaderStatus(state, TrayPanelPalette.Dark);
        List<LinuxTrayMenuItem> items = [new($"Aspire \u00b7 {summary}", Enabled: false)];
        var notice = state.Notice ?? (state.Discovery != DiscoveryState.Live || state.AppHosts.Any(host => host.Error is not null)
            ? state.Status : null);
        if (!string.IsNullOrEmpty(notice) && notice != summary)
        {
            items.Add(new(notice, Enabled: false));
        }
        AddSection("RUNNING", state.AppHosts.Where(host => host.Id.AppHostPid != 0));
        AddSection("PINNED", state.AppHosts.Where(host => host.Id.AppHostPid == 0));
        if (state.RecentAppHosts.Count > 0)
        {
            items.Add(new("", LinuxTrayAction.Separator));
            var recent = state.RecentAppHosts.Select(host => Host(host, recent: true)).ToList();
            recent.Add(new("", LinuxTrayAction.Separator));
            recent.Add(new("Clear recent AppHosts...", LinuxTrayAction.ClearRecent, Enabled: state.CanClearRecent));
            items.Add(new($"Recently opened \u00b7 {state.RecentAppHosts.Count}", Children: recent));
        }
        items.Add(new("", LinuxTrayAction.Separator));
        items.Add(new("Settings...", LinuxTrayAction.Settings) { Icon = LinuxTrayIcon.Settings });
        items.Add(new("Documentation", LinuxTrayAction.Documentation) { Icon = LinuxTrayIcon.Documentation });
        items.Add(new("Quit", LinuxTrayAction.Quit));
        return items;

        void AddSection(string title, IEnumerable<AppHostMenuItem> hosts)
        {
            var rows = hosts.Select(host => Host(host, recent: false)).ToArray();
            if (rows.Length == 0)
            {
                return;
            }
            items.Add(new("", LinuxTrayAction.Separator));
            items.Add(new(title, Enabled: false));
            items.AddRange(rows);
        }

        LinuxTrayMenuItem Host(AppHostMenuItem host, bool recent)
        {
            List<LinuxTrayMenuItem> actions = [];
            if (host.IsRunning)
            {
                actions.Add(new("Open dashboard", LinuxTrayAction.Dashboard, host, host.CanOpenDashboard));
                actions.Add(new(host.IsStopping ? "Stopping..." : "Stop",
                    LinuxTrayAction.Stop, host, host.CanStop));
            }
            else
            {
                actions.Add(new(host.IsStarting ? "Starting..." : "Start", LinuxTrayAction.Start, host, host.CanStart));
            }
            actions.Add(new("", LinuxTrayAction.Separator));
            if (applications.Count > 0)
            {
                actions.Add(new("Open in", Children: applications.Select(application =>
                    new LinuxTrayMenuItem(application.Title, LinuxTrayAction.OpenIn, host) { Application = application }).ToArray()));
            }
            actions.Add(new("Show in file manager", LinuxTrayAction.Folder, host));
            actions.Add(new("Copy path", LinuxTrayAction.CopyPath, host));
            actions.Add(new(host.IsPinned ? "Unpin" : "Pin", LinuxTrayAction.TogglePin, host));
            if (recent)
            {
                actions.Add(new("Remove from recent", LinuxTrayAction.RemoveRecent, host));
            }
            actions.Add(new("", LinuxTrayAction.Separator));
            actions.Add(new(host.Error ?? Status(host), Enabled: false));
            var location = AppHostPresentation.GetCompactLocation(host.Id.AppHostPath);
            actions.Add(new(host.IsRunning ? $"{location} \u00b7 PID {host.Id.AppHostPid}" : location, Enabled: false));
            // D-Bus menu hosts vary in icon support. Text also makes health available
            // to screen readers and preserves it on hosts that omit menu artwork.
            var status = state.Discovery != DiscoveryState.Live ? "Discovery unavailable"
                : Status(host);
            return new($"{host.Title} \u00b7 {status}", Host: host, Children: actions)
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
                AppHostHealth.NoResources => "No resources",
                _ => "Health unknown"
            };
}
