// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal interface IAppHostClient
{
    IAsyncEnumerable<AppHostSnapshot> WatchAsync(CancellationToken cancellationToken);
    Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken);
    Task<StartResult> StartAsync(string appHostPath, CancellationToken cancellationToken);
}

internal readonly record struct AppHostId(string AppHostPath, int AppHostPid, long? ProcessStartTimeUnixMilliseconds);

internal enum DiscoveryState
{
    Connecting,
    Live,
    Disconnected,
    Incompatible,
    LimitExceeded
}

internal sealed record AppHostSnapshot(IReadOnlyList<AppHostInfo> AppHosts, DiscoveryState Discovery);

internal enum StopOutcome
{
    Stopped,
    NotFound,
    Ambiguous,
    IdentityMismatch,
    IdentityUnavailable,
    Failed,
    TimedOut,
    Incompatible
}

internal sealed record StopResult(StopOutcome Outcome, int? ExitCode);

internal enum StartOutcome
{
    Started,
    NotFound,
    Failed,
    TimedOut
}

internal sealed record StartResult(StartOutcome Outcome, int? ExitCode);

internal sealed record AppHostMenuItem(
    AppHostId Id,
    string Title,
    string Subtitle,
    string DisplayName,
    bool CanOpenDashboard,
    bool CanStop,
    bool IsStopping,
    string? Error)
{
    public bool IsPinned { get; init; }
    public bool CanStart { get; init; }
    public bool IsStarting { get; init; }
    public bool IsRunning { get; init; } = true;
    public AppHostHealth Health { get; init; }
}

internal sealed record TrayViewState(
    DiscoveryState Discovery,
    IReadOnlyList<AppHostMenuItem> AppHosts,
    string Status)
{
    public IReadOnlyList<AppHostMenuItem> RecentAppHosts { get; init; } = [];
    public bool CanClearRecent { get; init; }
    public bool HasActiveAppHosts { get; init; } = AppHosts.Any(host => host.IsRunning || host.IsStarting);
    public bool ShowStatus { get; init; } = AppHosts.Count == 0 || Discovery != DiscoveryState.Live
        || AppHosts.Any(host => host.Error is not null);
}
