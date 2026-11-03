// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Native.Runtime;

namespace Aspire.Hosting.Native.Dcp;

[JsonConverter(typeof(DcpProcessStateConverter))]
internal enum DcpProcessState
{
    Unknown, Pending, Building, Starting, Running, Paused, Stopping, Exited, Finished, Terminated, FailedToStart, RuntimeUnhealthy
}

[JsonConverter(typeof(DcpExecutionStateConverter))]
internal enum DcpExecutionState { Unknown, Running, Stopping, CleaningResources, CleanupComplete }

[JsonConverter(typeof(DcpResourceCleanupConverter))]
internal enum DcpResourceCleanup { Full, None }

internal enum DcpResourceCollection { Containers, Executables, Services }

[JsonConverter(typeof(DcpAddressAllocationModeConverter))]
internal enum DcpAddressAllocationMode { Proxyless, Localhost }

[JsonConverter(typeof(DcpExecutionTypeConverter))]
internal enum DcpExecutionType { Process }

internal sealed class DcpResourceCleanupConverter() : JsonStringEnumConverter<DcpResourceCleanup>(allowIntegerValues: false);
internal sealed class DcpAddressAllocationModeConverter() : JsonStringEnumConverter<DcpAddressAllocationMode>(allowIntegerValues: false);
internal sealed class DcpExecutionTypeConverter() : JsonStringEnumConverter<DcpExecutionType>(allowIntegerValues: false);

/// <summary>Separates DCP's extensible status vocabulary from native lifecycle decisions.</summary>
internal static class DcpStates
{
    public static string ToPath(this DcpResourceCollection collection) => collection switch
    {
        DcpResourceCollection.Containers => "containers",
        DcpResourceCollection.Executables => "executables",
        DcpResourceCollection.Services => "services",
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };

    public static string ToKind(this DcpResourceCollection collection) => collection switch
    {
        DcpResourceCollection.Containers => "Container",
        DcpResourceCollection.Executables => "Executable",
        DcpResourceCollection.Services => "Service",
        _ => throw new ArgumentOutOfRangeException(nameof(collection))
    };
    // DCP replies use {"status":{"state":"Running","exitCode":0}}.
    // Missing/new labels are Unknown; malformed JSON field types are errors.
    public static DcpProcessState ParseProcess(string? value) => value switch
    {
        "Pending" => DcpProcessState.Pending,
        "Building" => DcpProcessState.Building,
        "Starting" => DcpProcessState.Starting,
        "Running" => DcpProcessState.Running,
        "Paused" => DcpProcessState.Paused,
        "Stopping" => DcpProcessState.Stopping,
        "Exited" => DcpProcessState.Exited,
        "Finished" => DcpProcessState.Finished,
        "Terminated" => DcpProcessState.Terminated,
        "FailedToStart" => DcpProcessState.FailedToStart,
        "RuntimeUnhealthy" => DcpProcessState.RuntimeUnhealthy,
        _ => DcpProcessState.Unknown
    };

    public static bool IsTerminal(this DcpProcessState state) =>
        state is DcpProcessState.Exited or DcpProcessState.Finished or DcpProcessState.Terminated or DcpProcessState.FailedToStart;

    public static WorkloadStatus Observe(DcpStatus? status) => status?.State switch
    {
        DcpProcessState.Running => new(WorkloadPhase.Running, status.ExitCode),
        DcpProcessState.Exited or DcpProcessState.Finished or DcpProcessState.Terminated => new(WorkloadPhase.Exited, status.ExitCode),
        DcpProcessState.FailedToStart => new(WorkloadPhase.Failed, status.ExitCode),
        DcpProcessState.Pending or DcpProcessState.Building or DcpProcessState.Starting or DcpProcessState.Paused or
            DcpProcessState.Stopping or DcpProcessState.RuntimeUnhealthy => new(WorkloadPhase.Pending, status.ExitCode),
        _ => throw new InvalidDataException("DCP did not report a recognized workload state.")
    };

    public static DcpExecutionState ParseExecution(string? value) => value switch
    {
        "Running" => DcpExecutionState.Running,
        "Stopping" => DcpExecutionState.Stopping,
        "CleaningResources" => DcpExecutionState.CleaningResources,
        "CleanupComplete" => DcpExecutionState.CleanupComplete,
        _ => DcpExecutionState.Unknown
    };
}

internal sealed class WorkloadProtocolConverter : JsonConverter<WorkloadProtocol>
{
    public override WorkloadProtocol Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RuntimeStates.ParseProtocol(reader.GetString() ?? throw new JsonException("A protocol is required."));
    public override void Write(Utf8JsonWriter writer, WorkloadProtocol value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}

internal sealed class WorkloadMountKindConverter : JsonConverter<WorkloadMountKind>
{
    public override WorkloadMountKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        RuntimeStates.ParseMountKind(reader.GetString() ?? throw new JsonException("A mount kind is required."));
    public override void Write(Utf8JsonWriter writer, WorkloadMountKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWire());
}

internal sealed class DcpProcessStateConverter : JsonConverter<DcpProcessState>
{
    public override DcpProcessState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
        {
            throw new JsonException("DCP process state must be a string or null.");
        }

        return DcpStates.ParseProcess(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, DcpProcessState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DcpProcessState.Unknown => "Unknown",
            DcpProcessState.Pending => "Pending",
            DcpProcessState.Building => "Building",
            DcpProcessState.Starting => "Starting",
            DcpProcessState.Running => "Running",
            DcpProcessState.Paused => "Paused",
            DcpProcessState.Stopping => "Stopping",
            DcpProcessState.Exited => "Exited",
            DcpProcessState.Finished => "Finished",
            DcpProcessState.Terminated => "Terminated",
            DcpProcessState.FailedToStart => "FailedToStart",
            DcpProcessState.RuntimeUnhealthy => "RuntimeUnhealthy",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        });
    }
}

internal sealed class DcpExecutionStateConverter : JsonConverter<DcpExecutionState>
{
    public override DcpExecutionState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.Null))
        {
            throw new JsonException("DCP execution state must be a string or null.");
        }

        return DcpStates.ParseExecution(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, DcpExecutionState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            DcpExecutionState.Unknown => "Unknown",
            DcpExecutionState.Running => "Running",
            DcpExecutionState.Stopping => "Stopping",
            DcpExecutionState.CleaningResources => "CleaningResources",
            DcpExecutionState.CleanupComplete => "CleanupComplete",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        });
    }
}
