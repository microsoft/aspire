// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Native.Auxiliary;

// BCL-only subsets of BackchannelDataTypes.cs. No ATS annotations: this is the existing
// Hosting/CLI wire contract, not an extension to the generated native integration API.
internal sealed record NativeAuxiliaryEmptyRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
}
internal sealed record NativeAuxiliaryTraceContext(string? TraceParent, string? TraceState, Dictionary<string, string>? Baggage);
internal sealed record NativeAuxiliaryResourcesRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public string? Filter { get; init; }
    public string[] ClientCapabilities { get; init; } = [];
}
internal sealed record NativeAuxiliaryLogsRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public string? ResourceName { get; init; }
    public bool Follow { get; init; }
    public string? Search { get; init; }
    public int? Tail { get; init; }
    public bool IncludeHidden { get; init; }
}
internal sealed record NativeAuxiliaryCommandRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public required string ResourceName { get; init; }
    public required string CommandName { get; init; }
    public JsonElement? Arguments { get; init; }
    public bool ValidateOnly { get; init; }
    public bool NonInteractive { get; init; } = true;
    public bool ReturnArgumentInputs { get; init; }
}
internal sealed record NativeAuxiliaryWaitRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public required string ResourceName { get; init; }
    public required string Status { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
}
internal sealed record NativeAuxiliaryMcpRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public required string ResourceName { get; init; }
    public required string ToolName { get; init; }
    public JsonElement? Arguments { get; init; }
}
internal sealed record NativeAuxiliaryTerminalRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public required string ResourceName { get; init; }
}
internal sealed record NativeAuxiliaryStopRequest
{
    public NativeAuxiliaryTraceContext? TraceContext { get; init; }
    public int? ExitCode { get; init; }
}
internal sealed record NativeAuxiliaryCapabilities(string[] Capabilities);
internal sealed record NativeAuxiliaryAppHostInformation
{
    public required string AppHostPath { get; init; }
    public int ProcessId { get; init; }
    public int? CliProcessId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StableStartedAt { get; init; }
    public DateTimeOffset? CliStartedAt { get; init; }
    public DateTimeOffset? CliStableStartedAt { get; init; }
    public string? CliLogFilePath { get; init; }
}
internal sealed record NativeAuxiliaryAppHostInfo(string Pid, string AspireHostVersion, string AppHostPath,
    int? CliProcessId, DateTimeOffset? StartedAt, string? CliLogFilePath);
internal sealed record NativeAuxiliaryDashboardInfo(string? ApiBaseUrl, string? ApiToken, string[] DashboardUrls, bool IsHealthy);
internal sealed record NativeAuxiliaryReady(bool IsReady);
internal sealed record NativeAuxiliaryResources(NativeAuxiliaryResourceSnapshot[] Resources);
internal sealed record NativeAuxiliaryEmptyResponse;
internal sealed record NativeAuxiliaryCommandResponse
{
    public required bool Success { get; init; }
    public bool Canceled { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Message { get; init; }
    public NativeAuxiliaryCommandResult? Value { get; init; }
    public NativeAuxiliaryValidationError[] ValidationErrors { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeAuxiliaryCommandArgument[]? ArgumentInputs { get; init; }
}
internal sealed record NativeAuxiliaryCommandResult(string Value, string Format, bool DisplayImmediately);
internal sealed record NativeAuxiliaryValidationError(string ArgumentName, string ErrorMessage);
internal sealed record NativeAuxiliaryWaitResponse
{
    public required bool Success { get; init; }
    public string? State { get; init; }
    public string? HealthStatus { get; init; }
    public bool ResourceNotFound { get; init; }
    public bool TimedOut { get; init; }
    public string? ErrorMessage { get; init; }
}
internal sealed record NativeAuxiliaryTerminalInfo(bool IsAvailable = false, string? SocketPath = null,
    int Columns = 0, int Rows = 0, NativeAuxiliaryEmptyResponse[]? Replicas = null);
internal sealed record NativeAuxiliaryTerminals
{
    public NativeAuxiliaryEmptyResponse[] ResourceTerminals { get; init; } = [];
    public NativeAuxiliaryEmptyResponse[] AppHostTerminals { get; init; } = [];
}
internal sealed record NativeAuxiliaryLogLine(string ResourceName, int LineNumber, string Content, bool IsError);
internal sealed record NativeAuxiliaryLogBatch(NativeAuxiliaryLogLine[] Lines);
internal sealed record NativeAuxiliaryResourceSnapshot
{
    public required string Name { get; init; }
    public long Version { get; init; }
    public string? DisplayName { get; init; }
    public string? ResourceType { get; init; }
    public string? Type => ResourceType;
    public string? State { get; init; }
    public string[]? WaitingFor { get; init; }
    public string? StateStyle { get; init; }
    public string? HealthStatus { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StoppedAt { get; init; }
    public NativeAuxiliaryUrl[] Urls { get; init; } = [];
    public NativeAuxiliaryRelationship[] Relationships { get; init; } = [];
    public NativeAuxiliaryHealthReport[] HealthReports { get; init; } = [];
    public NativeAuxiliaryVolume[] Volumes { get; init; } = [];
    public NativeAuxiliaryEnvironmentVariable[] EnvironmentVariables { get; init; } = [];
    public Dictionary<string, JsonElement> Properties { get; init; } = [];
    public bool IsHidden { get; init; }
    public NativeAuxiliaryEmptyResponse? McpServer { get; init; }
    public NativeAuxiliaryCommand[] Commands { get; init; } = [];
}
internal sealed record NativeAuxiliaryUrl(string Name, string Url, bool IsInternal = false,
    NativeAuxiliaryUrlDisplayProperties? DisplayProperties = null);
internal sealed record NativeAuxiliaryUrlDisplayProperties(string? DisplayName, int SortOrder);
internal sealed record NativeAuxiliaryRelationship(string ResourceName, string Type);
internal sealed record NativeAuxiliaryHealthReport(string Name, string? Description, string? Status, string? ExceptionText);
internal sealed record NativeAuxiliaryVolume(string? Source, string Target, string MountType, bool IsReadOnly);
internal sealed record NativeAuxiliaryEnvironmentVariable(string Name, string? Value, bool IsFromSpec);
internal sealed record NativeAuxiliaryCommand
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public NativeAuxiliaryCommandArgument[] ArgumentInputs { get; init; } = [];
    public string Visibility { get; init; } = "UI, Api";
    public required string State { get; init; }
}
// Native commands have no argument declarations. This named type keeps the empty arrays
// extensible without pulling managed command execution or interaction implementations into AOT.
internal sealed record NativeAuxiliaryCommandArgument(string Name, string InputType);

[JsonSerializable(typeof(NativeAuxiliaryEmptyRequest))]
[JsonSerializable(typeof(NativeAuxiliaryResourcesRequest))]
[JsonSerializable(typeof(NativeAuxiliaryLogsRequest))]
[JsonSerializable(typeof(NativeAuxiliaryCommandRequest))]
[JsonSerializable(typeof(NativeAuxiliaryWaitRequest))]
[JsonSerializable(typeof(NativeAuxiliaryMcpRequest))]
[JsonSerializable(typeof(NativeAuxiliaryTerminalRequest))]
[JsonSerializable(typeof(NativeAuxiliaryStopRequest))]
[JsonSerializable(typeof(NativeAuxiliaryCapabilities))]
[JsonSerializable(typeof(NativeAuxiliaryAppHostInformation))]
[JsonSerializable(typeof(NativeAuxiliaryAppHostInfo))]
[JsonSerializable(typeof(NativeAuxiliaryDashboardInfo))]
[JsonSerializable(typeof(NativeAuxiliaryReady))]
[JsonSerializable(typeof(NativeAuxiliaryResources))]
[JsonSerializable(typeof(NativeAuxiliaryResourceSnapshot[]))]
[JsonSerializable(typeof(NativeAuxiliaryResourceSnapshot))]
[JsonSerializable(typeof(NativeAuxiliaryCommandResponse))]
[JsonSerializable(typeof(NativeAuxiliaryWaitResponse))]
[JsonSerializable(typeof(NativeAuxiliaryEmptyResponse))]
[JsonSerializable(typeof(NativeAuxiliaryTerminalInfo))]
[JsonSerializable(typeof(NativeAuxiliaryTerminals))]
[JsonSerializable(typeof(NativeAuxiliaryLogLine))]
[JsonSerializable(typeof(NativeAuxiliaryLogBatch))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, RespectNullableAnnotations = true)]
internal sealed partial class NativeAuxiliaryJsonContext : JsonSerializerContext;
