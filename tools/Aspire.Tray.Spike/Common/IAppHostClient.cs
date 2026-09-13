// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Spike;

internal interface IAppHostClient
{
    IAsyncEnumerable<AppHostSnapshot> WatchAsync(CancellationToken cancellationToken);
    Task<StopResult> StopAsync(AppHostId id, CancellationToken cancellationToken);
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

internal sealed record AppHostMenuItem(
    AppHostId Id,
    string Title,
    string Subtitle,
    string DisplayName,
    bool CanOpenDashboard,
    bool CanStop,
    bool IsStopping,
    string? Error);

internal sealed record TrayViewState(
    DiscoveryState Discovery,
    IReadOnlyList<AppHostMenuItem> AppHosts,
    string Status);
