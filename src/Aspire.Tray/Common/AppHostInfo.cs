// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed record AppHostInfo(string AppHostPath, int AppHostPid, string? DashboardUrl)
{
    public long? ProcessStartTimeUnixMilliseconds { get; init; }
    public AppHostHealth Health { get; init; }

    public AppHostId Id => new(
        OperatingSystem.IsWindows() ? AppHostPath.ToUpperInvariant() : AppHostPath,
        AppHostPid, ProcessStartTimeUnixMilliseconds);

    public Uri? DashboardUri => Uri.TryCreate(DashboardUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) ? uri : null;
}

/// <summary>
/// Describes the aggregate health of an AppHost's applicable resources.
/// </summary>
internal enum AppHostHealth
{
    Unknown,
    Healthy,
    Warning,
    Unhealthy
}
