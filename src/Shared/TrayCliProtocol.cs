// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Shared;

/// <summary>
/// Defines the experimental, opt-in CLI protocol used by the native tray spike.
/// </summary>
internal static class TrayCliProtocol
{
    public const int Version = 1;
    public const int MaximumMessageLength = 1024 * 1024;
    public const int MaximumAppHosts = 1000;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LivenessTimeout = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Identifies a discovered AppHost, including its process lifetime when available.
/// </summary>
internal sealed record TrayAppHost
{
    public required string AppHostPath { get; init; }
    public required int AppHostPid { get; init; }
    public long? ProcessStartTimeUnixMilliseconds { get; init; }
    public string? DashboardUrl { get; init; }
}

/// <summary>
/// Carries a complete snapshot, a heartbeat, or a terminal discovery error.
/// </summary>
internal sealed record TrayWatchMessage
{
    public required int Version { get; init; }
    public required string Type { get; init; }
    public IReadOnlyList<TrayAppHost>? AppHosts { get; init; }
    public string? ErrorCode { get; init; }
}

/// <summary>
/// Reports an exact-instance stop outcome without requiring diagnostic text parsing.
/// </summary>
internal sealed record TrayStopMessage
{
    public required int Version { get; init; }
    public required string Outcome { get; init; }
    public required int ExitCode { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TrayWatchMessage))]
[JsonSerializable(typeof(TrayStopMessage))]
internal sealed partial class TrayCliJsonContext : JsonSerializerContext;
