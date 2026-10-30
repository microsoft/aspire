// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>
/// What the tray icon shows. Both platforms use the same rules: a plain logo when nothing
/// runs, a green dot while AppHosts run, an amber dot when something needs attention, and a
/// dimmed logo without a dot while discovery is connecting or unavailable.
/// </summary>
internal enum TrayIconStatus
{
    Idle,
    Running,
    Attention,
    Connecting,
    Unavailable
}

internal static class TrayIconStatusRules
{
    public static TrayIconStatus Get(TrayViewState state) => state.Discovery switch
    {
        DiscoveryState.Connecting => TrayIconStatus.Connecting,
        DiscoveryState.Live when !state.HasActiveAppHosts => TrayIconStatus.Idle,
        DiscoveryState.Live => NeedsAttention(state) ? TrayIconStatus.Attention : TrayIconStatus.Running,
        _ => TrayIconStatus.Unavailable
    };

    /// <summary>True when the logo is drawn dimmed and without a badge.</summary>
    public static bool IsDimmed(TrayIconStatus status) => status is TrayIconStatus.Connecting or TrayIconStatus.Unavailable;

    /// <summary>True when the logo carries a status dot (and the cut-out around it).</summary>
    public static bool HasBadge(TrayIconStatus status) => status is TrayIconStatus.Running or TrayIconStatus.Attention;

    public static string Describe(TrayIconStatus status) => status switch
    {
        TrayIconStatus.Running => "AppHosts running",
        TrayIconStatus.Attention => "AppHosts need attention",
        TrayIconStatus.Idle => "No AppHosts running",
        TrayIconStatus.Connecting => "Connecting to discovery",
        _ => "Discovery unavailable"
    };

    private static bool NeedsAttention(TrayViewState state)
        // Same signals as the panel header's "needs attention" count, plus the banner notice
        // (a storage, pin-probe or action error), which also deserves a look.
        => state.Notice is not null
            || state.AppHosts.Concat(state.RecentAppHosts).Any(host => host.Error is not null
                || (host.IsRunning && host.Id.AppHostPid != 0 && host.Health is AppHostHealth.Warning or AppHostHealth.Unhealthy));
}
