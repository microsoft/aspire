// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

/// <summary>Representative view states used by the panel tests and the preview images.</summary>
internal static class TrayPanelSamples
{
    public static AppHostMenuItem Running(string path, int pid, AppHostHealth health = AppHostHealth.Healthy, bool dashboard = true,
        string? error = null, bool stopping = false)
    {
        var id = new AppHostId(path, pid, 1_700_000_000_000 + pid);
        var info = new AppHostInfo(path, pid, dashboard ? "https://localhost:17000" : null);
        return new(id, AppHostPresentation.GetTitle(info), AppHostPresentation.GetSubtitle(info), AppHostPresentation.GetDisplayName(info),
            dashboard && !stopping, !stopping, stopping, error)
        {
            Health = health
        };
    }

    public static AppHostMenuItem Offline(string path, bool pinned = true, bool starting = false, string? error = null, string subtitle = "Stopped")
    {
        var info = new AppHostInfo(path, 0, null);
        return new(info.Id, AppHostPresentation.GetTitle(info), error ?? subtitle, AppHostPresentation.GetDisplayName(info),
            false, false, false, error)
        {
            IsPinned = pinned,
            CanStart = !starting,
            IsStarting = starting,
            IsRunning = false
        };
    }

    public static TrayViewState Typical() => new(DiscoveryState.Live,
        [
            Running("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", 48213),
            Running("/Users/dev/src/contoso/orders/AppHost/apphost.cs", 51877, AppHostHealth.Warning),
            Running("/Users/dev/src/samples/Redis.AppHost/Redis.AppHost.csproj", 52004, AppHostHealth.Unknown, dashboard: false),
            Offline("/Users/dev/src/weather/Weather.AppHost/Weather.AppHost.csproj")
        ],
        "3 AppHosts")
    {
        RecentAppHosts =
        [
            Offline("/Users/dev/src/todo/Todo.AppHost/Todo.AppHost.csproj", pinned: false),
            Offline("/Users/dev/src/old/Legacy.AppHost/Legacy.AppHost.csproj", pinned: false, subtitle: "AppHost source file not found")
        ],
        CanClearRecent = true
    };

    public static TrayViewState Empty() => new(DiscoveryState.Live, [], "No AppHosts running")
    {
        RecentAppHosts =
        [
            Offline("/Users/dev/src/todo/Todo.AppHost/Todo.AppHost.csproj", pinned: false),
            Offline("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", pinned: false)
        ],
        CanClearRecent = true
    };

    public static TrayViewState Connecting() => new(DiscoveryState.Connecting, [], "Connecting to Aspire...");

    public static TrayViewState Errors() => new(DiscoveryState.Live,
        [
            Running("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", 48213, AppHostHealth.Unhealthy),
            Running("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", 48977,
                error: "Stop timed out. The shutdown may still complete."),
            Running("/Users/dev/src/contoso/orders/AppHost/apphost.cs", 51877, stopping: true),
            Offline("/Users/dev/src/weather/Weather.AppHost/Weather.AppHost.csproj", starting: true, subtitle: "Starting AppHost; waiting for discovery...")
        ],
        "1 AppHost needs attention")
    {
        Notice = "Unable to save AppHost history. The saved file was left unchanged."
    };

    public static TrayViewState Disconnected() => new(DiscoveryState.Disconnected,
        [Running("/Users/dev/src/eShop/src/eShop.AppHost/eShop.AppHost.csproj", 48213)],
        "Discovery is unavailable. Reconnecting\u2026")
    {
        Notice = "Discovery is unavailable. Reconnecting…"
    };

    public static TrayViewState Many(int count = 14) => new(DiscoveryState.Live,
        Enumerable.Range(0, count).Select(i => Running($"/Users/dev/src/service{i:00}/Service{i:00}.AppHost/Service{i:00}.AppHost.csproj",
            40000 + i, (AppHostHealth)(i % 4))).ToArray(),
        $"{count} AppHosts")
    {
        RecentAppHosts = [Offline("/Users/dev/src/todo/Todo.AppHost/Todo.AppHost.csproj", pinned: false)],
        CanClearRecent = true
    };
}
