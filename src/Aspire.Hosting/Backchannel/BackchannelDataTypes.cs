// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// These types are source shared between the CLI and the Aspire.Hosting projects.
// The CLI sets the types in its own namespace.
#if CLI
namespace Aspire.Cli.Backchannel;
#else
namespace Aspire.Hosting.Backchannel;
#endif

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// =============================================================================
// Auxiliary Backchannel Contract Rules:
//
// 1. All methods take a single request object (nullable where sensible)
// 2. All methods return a response object (or IAsyncEnumerable<T> for streaming)
// 3. Request/response types are sealed classes with { get; init; } properties
// 4. Required properties use 'required' keyword
// 5. Optional properties are nullable (T?) - can be added without breaking
// 6. Empty request classes are allowed (for future expansion)
// 7. Method names: Get*Async, Watch*Async (streaming), Call*Async (actions)
// =============================================================================

#region Capability Constants

/// <summary>
/// Constants for auxiliary backchannel capability versions.
/// </summary>
internal static class AuxiliaryBackchannelCapabilities
{
    /// <summary>
    /// Version 1 capabilities (13.1 baseline): GetAppHostInformationAsync, GetDashboardMcpConnectionInfoAsync, StopAppHostAsync.
    /// </summary>
    public const string V1 = "aux.v1";

    /// <summary>
    /// Version 2 capabilities (13.2+): Request objects, new methods.
    /// </summary>
    public const string V2 = "aux.v2";
}

#endregion

#region V2 Request/Response Types

/// <summary>
/// Request for getting auxiliary backchannel capabilities.
/// </summary>
internal sealed class GetCapabilitiesRequest { }

/// <summary>
/// Response containing auxiliary backchannel capabilities.
/// </summary>
internal sealed class GetCapabilitiesResponse
{
    /// <summary>
    /// Gets the list of supported capability versions (e.g., "aux.v1", "aux.v2").
    /// </summary>
    public required string[] Capabilities { get; init; }
}

/// <summary>
/// Request for getting AppHost information.
/// </summary>
internal sealed class GetAppHostInfoRequest { }

/// <summary>
/// Response containing AppHost information.
/// </summary>
internal sealed class GetAppHostInfoResponse
{
    /// <summary>
    /// Gets the AppHost process ID.
    /// </summary>
    public required string Pid { get; init; }

    /// <summary>
    /// Gets the Aspire hosting version.
    /// </summary>
    public required string AspireHostVersion { get; init; }

    /// <summary>
    /// Gets the fully qualified path to the AppHost project.
    /// </summary>
    public required string AppHostPath { get; init; }

    /// <summary>
    /// Gets the CLI process ID if the AppHost was launched via the CLI.
    /// </summary>
    public int? CliProcessId { get; init; }

    /// <summary>
    /// Gets when the AppHost process started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>
/// Request for getting Dashboard information.
/// </summary>
internal sealed class GetDashboardInfoRequest { }

/// <summary>
/// Response containing Dashboard information.
/// </summary>
internal sealed class GetDashboardInfoResponse
{
    /// <summary>
    /// Gets the base URL of the Dashboard API (without login token).
    /// Use this for API calls like /api/telemetry/*.
    /// </summary>
    public string? ApiBaseUrl { get; init; }

    /// <summary>
    /// Gets the Dashboard API token for authenticated API calls.
    /// </summary>
    public string? ApiToken { get; init; }

    /// <summary>
    /// Gets the Dashboard URLs with login tokens.
    /// </summary>
    public required string[] DashboardUrls { get; init; }

    /// <summary>
    /// Gets whether the Dashboard is healthy.
    /// </summary>
    public bool IsHealthy { get; init; }
}

/// <summary>
/// Request for getting resource snapshots.
/// </summary>
internal sealed class GetResourcesRequest
{
    /// <summary>
    /// Gets an optional filter pattern for resource names.
    /// </summary>
    public string? Filter { get; init; }
}

/// <summary>
/// Response containing resource snapshots.
/// </summary>
internal sealed class GetResourcesResponse
{
    /// <summary>
    /// Gets the resource snapshots.
    /// </summary>
    public required ResourceSnapshot[] Resources { get; init; }
}

/// <summary>
/// Request for watching resource changes.
/// </summary>
internal sealed class WatchResourcesRequest
{
    /// <summary>
    /// Gets an optional filter pattern for resource names.
    /// </summary>
    public string? Filter { get; init; }
}

/// <summary>
/// Request for getting console logs.
/// </summary>
internal sealed class GetConsoleLogsRequest
{
    /// <summary>
    /// Gets the resource name to get logs for.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets whether to follow (stream) new log entries.
    /// </summary>
    public bool Follow { get; init; }
}

/// <summary>
/// Request for calling an MCP tool on a resource.
/// </summary>
internal sealed class CallMcpToolRequest
{
    /// <summary>
    /// Gets the resource name.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Gets the tool arguments.
    /// </summary>
    public JsonElement? Arguments { get; init; }
}

/// <summary>
/// Response from calling an MCP tool.
/// </summary>
internal sealed class CallMcpToolResponse
{
    /// <summary>
    /// Gets whether the tool call resulted in an error.
    /// </summary>
    public required bool IsError { get; init; }

    /// <summary>
    /// Gets the content items returned by the tool.
    /// </summary>
    public required McpToolContentItem[] Content { get; init; }
}

/// <summary>
/// Represents a content item returned by an MCP tool.
/// </summary>
internal sealed class McpToolContentItem
{
    /// <summary>
    /// Gets the content type (e.g., "text").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the text content.
    /// </summary>
    public string? Text { get; init; }
}

/// <summary>
/// Request for stopping the AppHost.
/// </summary>
internal sealed class StopAppHostRequest
{
    /// <summary>
    /// Gets the exit code to use when stopping.
    /// </summary>
    public int? ExitCode { get; init; }
}

/// <summary>
/// Response from stopping the AppHost.
/// </summary>
internal sealed class StopAppHostResponse { }

/// <summary>
/// Request for executing a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandRequest
{
    /// <summary>
    /// Gets the resource name (or resource ID for replicas).
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the command name (e.g., "start", "stop", "restart").
    /// </summary>
    public required string CommandName { get; init; }
}

/// <summary>
/// Response from executing a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandResponse
{
    /// <summary>
    /// Gets whether the command executed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets whether the command was canceled.
    /// </summary>
    public bool Canceled { get; init; }

    /// <summary>
    /// Gets the error message if the command failed.
    /// </summary>
    [Obsolete("Use Message instead.")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets the message associated with the command result.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the value produced by the command.
    /// </summary>
    public ExecuteResourceCommandResult? Value { get; init; }
}

/// <summary>
/// Value produced by a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandResult
{
    /// <summary>
    /// Gets the value data.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Gets the format of the value data.
    /// </summary>
    public CommandResultFormat Format { get; init; }

    /// <summary>
    /// Gets whether to immediately display the value in the dashboard.
    /// </summary>
    public bool DisplayImmediately { get; init; }
}

/// <summary>
/// Specifies the format of a command result.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CommandResultFormat>))]
internal enum CommandResultFormat
{
    /// <summary>
    /// Plain text result.
    /// </summary>
    [JsonStringEnumMemberName("text")]
    Text,

    /// <summary>
    /// JSON result.
    /// </summary>
    [JsonStringEnumMemberName("json")]
    Json,

    /// <summary>
    /// Markdown result.
    /// </summary>
    [JsonStringEnumMemberName("markdown")]
    Markdown
}

#endregion

#region Wait For Resource

/// <summary>
/// Request to wait for a resource to reach a target status.
/// </summary>
internal sealed class WaitForResourceRequest
{
    /// <summary>
    /// Gets the name of the resource to wait for.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the target status to wait for (e.g., "up", "healthy", "down").
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Gets the timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;
}

/// <summary>
/// Response from waiting for a resource.
/// </summary>
internal sealed class WaitForResourceResponse
{
    /// <summary>
    /// Gets whether the resource reached the target status.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets the current state of the resource.
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Gets the current health status of the resource.
    /// </summary>
    public string? HealthStatus { get; init; }

    /// <summary>
    /// Gets whether the resource was not found.
    /// </summary>
    public bool ResourceNotFound { get; init; }

    /// <summary>
    /// Gets whether the wait timed out.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Gets the error message if the wait failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

#endregion

/// <summary>
/// Represents the state of a resource reported via RPC.
/// </summary>
internal sealed class RpcResourceState
{
    /// <summary>
    /// Gets the name of the resource.
    /// </summary>
    public required string Resource { get; init; }

    /// <summary>
    /// Gets the type of the resource.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the state of the resource.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Gets the endpoints associated with the resource.
    /// </summary>
    public required string[] Endpoints { get; init; }

    /// <summary>
    /// Gets the health status of the resource.
    /// </summary>
    public string? Health { get; init; }
}

/// <summary>
/// Represents dashboard URLs for the running AppHost.
/// </summary>
internal sealed class DashboardUrlsState
{
    public bool DashboardHealthy { get; init; } = true;

    /// <summary>
    /// Gets the dashboard URL.
    /// When browser token authentication is enabled, this value includes the login token.
    /// </summary>
    public string? BaseUrlWithLoginToken { get; init; }

    /// <summary>
    /// Gets the Codespaces dashboard URL, if available.
    /// When browser token authentication is enabled, this value includes the login token.
    /// </summary>
    public string? CodespacesUrlWithLoginToken { get; init; }
}

/// <summary>
/// Envelope for publishing activities sent over the backchannel.
/// </summary>
internal sealed class PublishingActivity
{
    /// <summary>
    /// Gets the type discriminator for the publishing activity.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Gets the data containing all properties for the publishing activity.
    /// </summary>
    public required PublishingActivityData Data { get; init; }
}

/// <summary>
/// Common data for all publishing activities.
/// </summary>
internal sealed class PublishingActivityData
{
    /// <summary>
    /// Gets the unique identifier for the publishing activity.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the status text describing the publishing activity.
    /// </summary>
    public required string StatusText { get; init; }

    /// <summary>
    /// Gets the completion state of the publishing activity.
    /// </summary>
    public string CompletionState { get; init; } = CompletionStates.InProgress;

    /// <summary>
    /// Gets a value indicating whether the publishing activity is complete.
    /// </summary>
    public bool IsComplete => CompletionState is not CompletionStates.InProgress;

    /// <summary>
    /// Gets a value indicating whether the publishing activity encountered an error.
    /// </summary>
    public bool IsError => CompletionState is CompletionStates.CompletedWithError;

    /// <summary>
    /// Gets a value indicating whether the publishing activity completed with warnings.
    /// </summary>
    public bool IsWarning => CompletionState is CompletionStates.CompletedWithWarning;

    /// <summary>
    /// Gets the identifier of the step this task belongs to (only applicable for tasks).
    /// </summary>
    public string? StepId { get; init; }

    /// <summary>
    /// Gets the identifier of the parent step used for hierarchical step summaries.
    /// </summary>
    public string? ParentStepId { get; init; }

    /// <summary>
    /// Gets the hierarchical level of the step used for display purposes.
    /// Nullable for backwards compatibility with older app hosts that do not send hierarchy metadata.
    /// </summary>
    public int? HierarchyLevel { get; init; }

    /// <summary>
    /// Gets the optional completion message for tasks (appears as dimmed child text).
    /// </summary>
    public string? CompletionMessage { get; init; }

    /// <summary>
    /// Gets the pipeline summary information to display after pipeline completion.
    /// Each item carries its own key, value, and Markdown formatting flag.
    /// The list preserves the order items were added.
    /// </summary>
    public IReadOnlyList<BackchannelPipelineSummaryItem>? PipelineSummary { get; init; }

    /// <summary>
    /// Gets the input information for prompt activities, if available.
    /// </summary>
    public IReadOnlyList<PublishingPromptInput>? Inputs { get; init; }

    /// <summary>
    /// Gets the log level for log activities, if available.
    /// </summary>
    public string? LogLevel { get; init; }

    /// <summary>
    /// Gets the timestamp for log activities, if available.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Gets a value indicating whether markdown formatting is enabled for the publishing activity.
    /// </summary>
    public bool EnableMarkdown { get; init; } = true;
}

/// <summary>
/// Represents a single item in a pipeline summary for backchannel transport.
/// </summary>
internal sealed class BackchannelPipelineSummaryItem
{
    /// <summary>
    /// Gets the key or label for the summary item.
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Gets the string value for the summary item.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Gets a value indicating whether the value contains Markdown formatting.
    /// </summary>
    public bool EnableMarkdown { get; init; }
}

/// <summary>
/// Represents an input for a publishing prompt.
/// </summary>
internal sealed class PublishingPromptInput
{
    /// <summary>
    /// Gets the name for the input.
    /// Nullable for backwards compatibility with Aspire 9.5 and older app hosts.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the label for the input.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// Gets the type of the input.
    /// </summary>
    public required string InputType { get; init; }

    /// <summary>
    /// Gets a value indicating whether the input is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Gets the options for the input. Only used by select inputs.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Options { get; init; }

    /// <summary>
    /// Gets the default value for the input.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets the validation errors for the input.
    /// </summary>
    public IReadOnlyList<string>? ValidationErrors { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a custom choice is allowed.
    /// </summary>
    public bool AllowCustomChoice { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the state should be updated when the input value changes.
    /// </summary>
    public bool UpdateStateOnChange { get; init; }

    public bool Loading { get; init; }

    public bool Disabled { get; init; }
}

/// <summary>
/// Constants for publishing activity types.
/// </summary>
internal static class PublishingActivityTypes
{
    public const string Step = "step";
    public const string Task = "task";
    public const string PublishComplete = "publish-complete";
    public const string Prompt = "prompt";
    public const string Log = "log";
}

/// <summary>
/// Constants for completion state values.
/// </summary>
internal static class CompletionStates
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";
    public const string CompletedWithWarning = "CompletedWithWarning";
    public const string CompletedWithError = "CompletedWithError";
}

internal class BackchannelLogEntry
{
    public required EventId EventId { get; set; }
    public required LogLevel LogLevel { get; set; }
    public required string Message { get; set; }
    public required DateTimeOffset Timestamp { get; set; }
    public required string CategoryName { get; set; }
}

internal class CommandOutput
{
    public required string Text { get; init; }
    public bool IsErrorMessage { get; init; }
    public int? LineNumber { get; init; }
    /// <summary>
    /// Additional info about type of the message.
    /// Should be used for controlling the display style.
    /// </summary>
    public string? Type { get; init; }

    public int? ExitCode { get; init; }
}

internal class PublishingPromptInputAnswer
{
    public string? Name { get; set; }
    public string? Value { get; set; }
}

/// <summary>
/// Represents the connection information for the Dashboard MCP server.
/// </summary>
internal sealed class DashboardMcpConnectionInfo
{
    /// <summary>
    /// Gets or sets the endpoint URL for the Dashboard MCP server.
    /// </summary>
    public required string EndpointUrl { get; init; }

    /// <summary>
    /// Gets or sets the API token for authenticating with the Dashboard MCP server.
    /// </summary>
    public required string ApiToken { get; init; }
}

/// <summary>
/// Represents a snapshot of a resource in the application model, suitable for RPC communication.
/// Designed to be extensible - new fields can be added without breaking existing consumers.
/// </summary>
[DebuggerDisplay("Name = {Name}, ResourceType = {ResourceType}, State = {State}, Properties = {Properties.Count}")]
internal sealed class ResourceSnapshot
{
    /// <summary>
    /// Gets the unique name of the resource.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the display name of the resource.
    /// </summary>
    public string? DisplayName { get; init; }

    // ResourceType can't be required because older versions of the backchannel may not set it.
    /// <summary>
    /// Gets the type of the resource (e.g., "Project", "Container", "Executable").
    /// </summary>
    public string? ResourceType { get; init; }

    /// <summary>
    /// Gets the type of the resource (e.g., "Project", "Container", "Executable").
    /// </summary>
    [Obsolete("Use ResourceType property instead.")]
    public string? Type
    {
        get => ResourceType;
        init => ResourceType = value;
    }

    /// <summary>
    /// Gets the current state of the resource (e.g., "Running", "Stopped", "Starting").
    /// </summary>
    public string? State { get; init; }

    /// <summary>
    /// Gets the state style hint (e.g., "success", "error", "warning").
    /// </summary>
    public string? StateStyle { get; init; }

    /// <summary>
    /// Gets the health status of the resource (e.g., "Healthy", "Unhealthy", "Degraded").
    /// </summary>
    public string? HealthStatus { get; init; }

    /// <summary>
    /// Gets the exit code if the resource has exited.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Gets the creation timestamp of the resource.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// Gets the start timestamp of the resource.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Gets the stop timestamp of the resource.
    /// </summary>
    public DateTimeOffset? StoppedAt { get; init; }

    /// <summary>
    /// Gets the URLs exposed by this resource.
    /// </summary>
    public ResourceSnapshotUrl[] Urls { get; init; } = [];

    /// <summary>
    /// Gets the relationships to other resources.
    /// </summary>
    public ResourceSnapshotRelationship[] Relationships { get; init; } = [];

    /// <summary>
    /// Gets the health reports for this resource.
    /// </summary>
    public ResourceSnapshotHealthReport[] HealthReports { get; init; } = [];

    /// <summary>
    /// Gets the volumes mounted to this resource.
    /// </summary>
    public ResourceSnapshotVolume[] Volumes { get; init; } = [];

    /// <summary>
    /// Gets the environment variables for this resource.
    /// </summary>
    public ResourceSnapshotEnvironmentVariable[] EnvironmentVariables { get; init; } = [];

    /// <summary>
    /// Gets additional properties as key-value pairs.
    /// This allows for extensibility without changing the schema.
    /// </summary>
    public Dictionary<string, string?> Properties { get; init; } = [];

    /// <summary>
    /// Gets the MCP server information if the resource exposes an MCP endpoint.
    /// </summary>
    public ResourceSnapshotMcpServer? McpServer { get; init; }

    /// <summary>
    /// Gets the commands available for this resource.
    /// </summary>
    public ResourceSnapshotCommand[] Commands { get; init; } = [];
}

/// <summary>
/// Represents a command available for a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, State = {State}")]
internal sealed class ResourceSnapshotCommand
{
    /// <summary>
    /// Gets the command name (e.g., "start", "stop", "restart").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the display name of the command.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the description of the command.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the state of the command (e.g., "Enabled", "Disabled", "Hidden").
    /// </summary>
    public required string State { get; init; }
}

/// <summary>
/// Represents a URL exposed by a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, Url = {Url}")]
internal sealed class ResourceSnapshotUrl
{
    /// <summary>
    /// Gets the URL name (e.g., "http", "https", "tcp").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the full URL including scheme, host, and port.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets whether this is an internal URL.
    /// </summary>
    public bool IsInternal { get; init; }

    /// <summary>
    /// Gets the display properties for the URL.
    /// </summary>
    public ResourceSnapshotUrlDisplayProperties? DisplayProperties { get; init; }
}

/// <summary>
/// Represents display properties for a URL.
/// </summary>
[DebuggerDisplay("DisplayName = {DisplayName}, SortOrder = {SortOrder}")]
internal sealed class ResourceSnapshotUrlDisplayProperties
{
    /// <summary>
    /// Gets the display name of the URL.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the sort order for display. Higher numbers are displayed first.
    /// </summary>
    public int SortOrder { get; init; }
}

/// <summary>
/// Represents a relationship to another resource.
/// </summary>
[DebuggerDisplay("ResourceName = {ResourceName}, Type = {Type}")]
internal sealed class ResourceSnapshotRelationship
{
    /// <summary>
    /// Gets the name of the related resource.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the relationship type (e.g., "Parent", "Reference").
    /// </summary>
    public required string Type { get; init; }
}

/// <summary>
/// Represents a health report for a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, Status = {Status}")]
internal sealed class ResourceSnapshotHealthReport
{
    /// <summary>
    /// Gets the name of the health check.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the status (e.g., "Healthy", "Unhealthy", "Degraded").
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// Gets the description of the health report.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the exception text if the health check failed.
    /// </summary>
    public string? ExceptionText { get; init; }
}

/// <summary>
/// Represents a volume mounted to a resource.
/// </summary>
[DebuggerDisplay("Source = {Source}, Target = {Target}")]
internal sealed class ResourceSnapshotVolume
{
    /// <summary>
    /// Gets the source path or volume name.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the target path in the container.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// Gets the mount type (e.g., "bind", "volume").
    /// </summary>
    public required string MountType { get; init; }

    /// <summary>
    /// Gets whether the volume is read-only.
    /// </summary>
    public bool IsReadOnly { get; init; }
}

/// <summary>
/// Represents an environment variable for a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, Value = {Value}")]
internal sealed class ResourceSnapshotEnvironmentVariable
{
    /// <summary>
    /// Gets the name of the environment variable.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the value of the environment variable.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets whether this environment variable is from the resource specification.
    /// </summary>
    public bool IsFromSpec { get; init; }
}

/// <summary>
/// Represents MCP server information for a resource.
/// </summary>
[DebuggerDisplay("EndpointUrl = {EndpointUrl}")]
internal sealed class ResourceSnapshotMcpServer
{
    /// <summary>
    /// Gets the MCP endpoint URL.
    /// </summary>
    public required string EndpointUrl { get; init; }

    /// <summary>
    /// Gets the tools exposed by the MCP server.
    /// </summary>
    public required Tool[] Tools { get; init; }
}

/// <summary>
/// Represents information about the AppHost for the MCP server.
/// </summary>
internal sealed class AppHostInformation
{
    /// <summary>
    /// Gets or sets the fully qualified path to the AppHost project.
    /// </summary>
    public required string AppHostPath { get; init; }

    /// <summary>
    /// Gets or sets the process ID of the AppHost.
    /// </summary>
    public required int ProcessId { get; init; }

    /// <summary>
    /// Gets or sets the process ID of the CLI that launched the AppHost, if applicable.
    /// This value is only set when the AppHost is launched via the Aspire CLI.
    /// </summary>
    public int? CliProcessId { get; init; }

    /// <summary>
    /// Gets or sets when the AppHost process started.
    /// </summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>
    /// Gets or sets when the CLI process that launched the AppHost started.
    /// This value is only set when the AppHost is launched via the Aspire CLI.
    /// </summary>
    public DateTimeOffset? CliStartedAt { get; init; }
}

/// <summary>
/// Represents a log line from a resource's console output.
/// </summary>
internal sealed class ResourceLogLine
{
    /// <summary>
    /// Gets the name of the resource that produced this log line.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the line number within the log stream.
    /// </summary>
    public required int LineNumber { get; init; }

    /// <summary>
    /// Gets the content of the log line.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets whether this log line is from stderr (error output).
    /// </summary>
    public bool IsError { get; init; }
}
