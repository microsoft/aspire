// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Native.Runtime;

internal enum ResourceState { Unknown, Starting, Running, Failed, Stopped }
internal enum ConfigurationStatus { Pending, Succeeded, Failed }
internal enum RequestOutcome { Succeeded, Failed, Cancelled, Accepted, Rejected }
internal enum LogStream { Stdout, Stderr }
internal enum WorkloadProtocol { Tcp, Udp }
internal enum WorkloadMountKind { Bind, Volume }

/// <summary>Parses the closed native runtime vocabulary independently of orchestration.</summary>
internal static class RuntimeStates
{
    // ATS carries PascalCase lifecycle labels and lowercase result/log labels:
    // {"state":"Running"}, {"status":"succeeded"}, {"stream":"stdout"}.
    // Parse exact labels, never numeric enum values or case-insensitive aliases.
    public static ResourceState ParseObservation(string value) => value switch
    {
        "Starting" => ResourceState.Starting,
        "Running" => ResourceState.Running,
        "Failed" => ResourceState.Failed,
        "Stopped" => ResourceState.Stopped,
        _ => throw new ArgumentException("Invalid resource observation.", nameof(value))
    };

    public static ConfigurationStatus ParseConfigurationResult(string value) => value switch
    {
        "succeeded" => ConfigurationStatus.Succeeded,
        "failed" => ConfigurationStatus.Failed,
        _ => throw new ArgumentException("Invalid configuration result.", nameof(value))
    };

    public static RequestOutcome ParseCommandResult(string value) => value switch
    {
        "succeeded" => RequestOutcome.Succeeded,
        "failed" => RequestOutcome.Failed,
        "cancelled" => RequestOutcome.Cancelled,
        _ => throw new ArgumentException("Invalid command result.", nameof(value))
    };

    public static LogStream ParseLogStream(string value) => value switch
    {
        "stdout" => LogStream.Stdout,
        "stderr" => LogStream.Stderr,
        _ => throw new ArgumentException("Invalid resource log entry.", nameof(value))
    };

    public static WorkloadProtocol ParseProtocol(string value) => value switch
    {
        "TCP" => WorkloadProtocol.Tcp,
        "UDP" => WorkloadProtocol.Udp,
        _ => throw new ArgumentException("Invalid workload endpoint protocol.", nameof(value))
    };

    public static WorkloadMountKind ParseMountKind(string value) => value switch
    {
        "bind" => WorkloadMountKind.Bind,
        "volume" => WorkloadMountKind.Volume,
        _ => throw new ArgumentException("Invalid workload mount kind.", nameof(value))
    };

    public static string ToWire(this WorkloadProtocol protocol) => protocol switch
    {
        WorkloadProtocol.Tcp => "TCP",
        WorkloadProtocol.Udp => "UDP",
        _ => throw new ArgumentOutOfRangeException(nameof(protocol))
    };

    public static string ToWire(this WorkloadMountKind kind) => kind switch
    {
        WorkloadMountKind.Bind => "bind",
        WorkloadMountKind.Volume => "volume",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsTerminal(this ResourceState state) => state is ResourceState.Stopped or ResourceState.Failed;

    public static string ToWire(this ResourceState state) => state switch
    {
        ResourceState.Unknown => "Unknown",
        ResourceState.Starting => "Starting",
        ResourceState.Running => "Running",
        ResourceState.Failed => "Failed",
        ResourceState.Stopped => "Stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    public static string ToWire(this ConfigurationStatus status) => status switch
    {
        ConfigurationStatus.Pending => "pending",
        ConfigurationStatus.Succeeded => "succeeded",
        ConfigurationStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    public static string ToWire(this RequestOutcome outcome) => outcome switch
    {
        RequestOutcome.Succeeded => "succeeded",
        RequestOutcome.Failed => "failed",
        RequestOutcome.Cancelled => "cancelled",
        RequestOutcome.Accepted => "accepted",
        RequestOutcome.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public static string ToWire(this LogStream stream) => stream switch
    {
        LogStream.Stdout => "stdout",
        LogStream.Stderr => "stderr",
        _ => throw new ArgumentOutOfRangeException(nameof(stream))
    };
}
