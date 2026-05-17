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
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// =============================================================================
// Auxiliary Backchannel Contract Rules:
//
// 1. All methods take a single request object (nullable where sensible)
// 2. All methods return a response object (or IAsyncEnumerable<T> for streaming)
// 3. Request types derive from BackchannelRequest; request/response types are sealed classes with { get; init; } properties
// 4. Required properties are reserved for fields present since the method shipped
//    and are validated immediately after deserialization at the RPC boundary
// 5. Optional properties are nullable (T?) - can be added without breaking
// 6. Optional collections use backing fields so missing/null wire values mean empty
// 7. Empty request classes are allowed (for future expansion)
// 8. Method names: Get*Async, Watch*Async (streaming), Call*Async (actions)
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

    /// <summary>
    /// Version 3 capabilities: Batched console log streaming.
    /// </summary>
    public const string V3 = "aux.v3";

}

/// <summary>
/// Constants for resource command visibility values in the auxiliary backchannel contract.
/// </summary>
internal static class KnownCommandVisibility
{
    public const string UI = "UI";
    public const string Api = "Api";
    public const string Default = $"{UI}, {Api}";
}

#endregion

#region V2 Request/Response Types

/// <summary>
/// Trace context metadata propagated over the auxiliary backchannel.
/// </summary>
internal sealed class BackchannelTraceContext
{
    private Dictionary<string, string> _baggage = [];

    /// <summary>
    /// Gets the baggage values associated with the trace.
    /// </summary>
    public Dictionary<string, string> Baggage
    {
        get => _baggage;
        init => _baggage = value ?? [];
    }
}

/// <summary>
/// Base class for auxiliary backchannel request-object RPC parameters.
/// </summary>
internal abstract class BackchannelRequest
{
    /// <summary>
    /// Gets trace context metadata propagated by the CLI.
    /// </summary>
    public BackchannelTraceContext? TraceContext { get; init; }

    /// <summary>
    /// Creates a copy of this request with the specified trace context.
    /// </summary>
    /// <remarks>
    /// StreamJsonRpc carries W3C traceparent/tracestate on the JSON-RPC request envelope.
    /// See https://microsoft.github.io/vs-streamjsonrpc/docs/resiliency.html#activity-tracing.
    /// The request object only carries extra trace metadata such as baggage values. Each
    /// request type owns its copy logic so this stays AOT- and trimming-friendly instead of
    /// relying on reflection to clone arbitrary records/classes.
    /// </remarks>
    public abstract BackchannelRequest WithTraceContext(BackchannelTraceContext traceContext);
}

/// <summary>
/// Request for getting auxiliary backchannel capabilities.
/// </summary>
internal sealed class GetCapabilitiesRequest : BackchannelRequest
{
    /// <inheritdoc />
    public override GetCapabilitiesRequest WithTraceContext(BackchannelTraceContext traceContext) => new() { TraceContext = traceContext };
}

/// <summary>
/// Response containing auxiliary backchannel capabilities.
/// </summary>
internal sealed class GetCapabilitiesResponse
{
    private string[] _capabilities = [];

    /// <summary>
    /// Gets the list of supported capability versions (e.g., "aux.v1", "aux.v2").
    /// </summary>
    public string[] Capabilities
    {
        get => _capabilities;
        init => _capabilities = value ?? [];
    }
}

/// <summary>
/// Request for getting AppHost information.
/// </summary>
internal sealed class GetAppHostInfoRequest : BackchannelRequest
{
    /// <inheritdoc />
    public override GetAppHostInfoRequest WithTraceContext(BackchannelTraceContext traceContext) => new() { TraceContext = traceContext };
}

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
internal sealed class GetDashboardInfoRequest : BackchannelRequest
{
    /// <inheritdoc />
    public override GetDashboardInfoRequest WithTraceContext(BackchannelTraceContext traceContext) => new() { TraceContext = traceContext };
}

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
    public string[]? DashboardUrls { get; init; }

    /// <summary>
    /// Gets whether the Dashboard is healthy.
    /// </summary>
    public bool IsHealthy { get; init; }
}

/// <summary>
/// Request for getting resource snapshots.
/// </summary>
internal sealed class GetResourcesRequest : BackchannelRequest
{
    /// <summary>
    /// Gets an optional filter pattern for resource names.
    /// </summary>
    public string? Filter { get; init; }

    /// <inheritdoc />
    public override GetResourcesRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        Filter = Filter
    };
}

/// <summary>
/// Response containing resource snapshots.
/// </summary>
internal sealed class GetResourcesResponse
{
    private ResourceSnapshot[] _resources = [];

    /// <summary>
    /// Gets the resource snapshots.
    /// </summary>
    public ResourceSnapshot[] Resources
    {
        get => _resources;
        init => _resources = value ?? [];
    }
}

/// <summary>
/// Request for watching resource changes.
/// </summary>
internal sealed class WatchResourcesRequest : BackchannelRequest
{
    /// <summary>
    /// Gets an optional filter pattern for resource names.
    /// </summary>
    public string? Filter { get; init; }

    /// <inheritdoc />
    public override WatchResourcesRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        Filter = Filter
    };
}

/// <summary>
/// Request for getting console logs.
/// </summary>
internal sealed class GetConsoleLogsRequest : BackchannelRequest
{
    /// <summary>
    /// Gets the resource name to get logs for.
    /// </summary>
    public string? ResourceName { get; init; }

    /// <summary>
    /// Gets whether to follow (stream) new log entries.
    /// </summary>
    public bool Follow { get; init; }

    /// <summary>
    /// Gets an optional search string to match against log content or resource name.
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// Gets the maximum number of matching snapshot log entries to return.
    /// </summary>
    public int? Tail { get; init; }

    /// <summary>
    /// Gets whether hidden resources should be included when no resource name is specified.
    /// </summary>
    public bool IncludeHidden { get; init; }

    /// <inheritdoc />
    public override GetConsoleLogsRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        ResourceName = ResourceName,
        Follow = Follow,
        Search = Search,
        Tail = Tail,
        IncludeHidden = IncludeHidden
    };
}

/// <summary>
/// Request for calling an MCP tool on a resource.
/// </summary>
internal sealed class CallMcpToolRequest : BackchannelRequest
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

    /// <inheritdoc />
    public override CallMcpToolRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        ResourceName = ResourceName,
        ToolName = ToolName,
        Arguments = Arguments
    };
}

/// <summary>
/// Response from calling an MCP tool.
/// </summary>
internal sealed class CallMcpToolResponse
{
    private McpToolContentItem[] _content = [];

    /// <summary>
    /// Gets whether the tool call resulted in an error.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Gets the content items returned by the tool.
    /// </summary>
    public McpToolContentItem[] Content
    {
        get => _content;
        init => _content = value ?? [];
    }
}

/// <summary>
/// Represents a content item returned by an MCP tool.
/// </summary>
internal sealed class McpToolContentItem
{
    private string _type = string.Empty;

    /// <summary>
    /// Gets the content type (e.g., "text").
    /// </summary>
    public string Type
    {
        get => _type;
        init => _type = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the text content.
    /// </summary>
    public string? Text { get; init; }
}

/// <summary>
/// Request for stopping the AppHost.
/// </summary>
internal sealed class StopAppHostRequest : BackchannelRequest
{
    /// <summary>
    /// Gets the exit code to use when stopping.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <inheritdoc />
    public override StopAppHostRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        ExitCode = ExitCode
    };
}

/// <summary>
/// Response from stopping the AppHost.
/// </summary>
internal sealed class StopAppHostResponse { }

/// <summary>
/// Request for executing a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandRequest : BackchannelRequest
{
    /// <summary>
    /// Gets the resource name (or resource ID for replicas).
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// Gets the command name (e.g., "start", "stop", "restart").
    /// </summary>
    public required string CommandName { get; init; }

    /// <summary>
    /// Gets optional invocation arguments to pass to the resource command.
    /// Arrays are matched to declared command arguments by order. Objects are matched by argument name.
    /// </summary>
    public JsonNode? Arguments { get; init; }

    /// <summary>
    /// Gets a value indicating whether the request should validate arguments without executing the command.
    /// </summary>
    public bool ValidateOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether command execution should fail instead of prompting for missing input.
    /// </summary>
    public bool NonInteractive { get; init; } = true;

    /// <inheritdoc />
    public override ExecuteResourceCommandRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        ResourceName = ResourceName,
        CommandName = CommandName,
        Arguments = Arguments,
        ValidateOnly = ValidateOnly,
        NonInteractive = NonInteractive
    };
}

/// <summary>
/// Options for executing a resource command through the auxiliary backchannel.
/// </summary>
internal sealed class ExecuteResourceCommandOptions
{
    /// <summary>
    /// Gets optional invocation arguments to pass to the resource command.
    /// Arrays are matched to declared command arguments by order. Objects are matched by argument name.
    /// </summary>
    public JsonNode? Arguments { get; init; }

    /// <summary>
    /// Gets a value indicating whether the request should validate arguments without executing the command.
    /// </summary>
    public bool ValidateOnly { get; init; }

    /// <summary>
    /// Gets a value indicating whether command execution should fail instead of prompting for missing input.
    /// </summary>
    public bool NonInteractive { get; init; } = true;
}

/// <summary>
/// Response from executing a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandResponse
{
    private ResourceCommandArgumentValidationError[] _validationErrors = [];

    // Retired JSON properties:
    // - "ErrorMessage": legacy name for "Message"; keep reading it for old AppHost payloads.

    /// <summary>
    /// Gets whether the command executed successfully.
    /// Missing values from older payloads default to false so consumers handle the
    /// response as a failure instead of failing during deserialization.
    /// </summary>
    public bool Success { get; init; }

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

    /// <summary>
    /// Gets validation errors for submitted command arguments.
    /// </summary>
    public ResourceCommandArgumentValidationError[] ValidationErrors
    {
        get => _validationErrors;
        init => _validationErrors = value ?? [];
    }
}

/// <summary>
/// Represents a validation error for a submitted resource command argument.
/// </summary>
internal sealed class ResourceCommandArgumentValidationError
{
    private string _argumentName = string.Empty;
    private string _errorMessage = string.Empty;

    /// <summary>
    /// Gets the argument name.
    /// </summary>
    public string ArgumentName
    {
        get => _argumentName;
        init => _argumentName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the validation error message.
    /// </summary>
    public string ErrorMessage
    {
        get => _errorMessage;
        init => _errorMessage = value ?? string.Empty;
    }
}

/// <summary>
/// Value produced by a resource command.
/// </summary>
internal sealed class ExecuteResourceCommandResult
{
    private CommandResultFormat _format = CommandResultFormat.None;

    /// <summary>
    /// Gets the value data.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets the format of the value data.
    /// </summary>
    [JsonConverter(typeof(CommandResultFormatJsonConverter))]
    public CommandResultFormat Format
    {
        get => _format;
        init => _format = value;
    }

    /// <summary>
    /// Gets whether to immediately display the value in the dashboard.
    /// </summary>
    public bool DisplayImmediately { get; init; }
}

/// <summary>
/// Specifies the format of a command result.
/// </summary>
[JsonConverter(typeof(CommandResultFormatJsonConverter))]
internal enum CommandResultFormat
{
    /// <summary>
    /// No specific result format was supplied by the remote peer.
    /// </summary>
    [JsonStringEnumMemberName("none")]
    None = 0,

    /// <summary>
    /// Plain text result.
    /// </summary>
    [JsonStringEnumMemberName("text")]
    Text = 1,

    /// <summary>
    /// JSON result.
    /// </summary>
    [JsonStringEnumMemberName("json")]
    Json = 2,

    /// <summary>
    /// Markdown result.
    /// </summary>
    [JsonStringEnumMemberName("markdown")]
    Markdown = 3
}

internal sealed class CommandResultFormatJsonConverter : JsonConverter<CommandResultFormat>
{
    public override bool HandleNull => true;

    public override CommandResultFormat Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => CommandResultFormat.None,
            JsonTokenType.String => ReadStringValue(reader.GetString()),
            JsonTokenType.Number when reader.TryGetInt32(out var value) => value switch
            {
                // Numeric values are legacy wire values from before the "none" sentinel existed.
                0 => CommandResultFormat.Text,
                1 => CommandResultFormat.Json,
                2 => CommandResultFormat.Markdown,
                _ => CommandResultFormat.None
            },
            _ => throw new JsonException($"The JSON token '{reader.TokenType}' cannot be converted to {nameof(CommandResultFormat)}.")
        };
    }

    public override void Write(Utf8JsonWriter writer, CommandResultFormat value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            CommandResultFormat.Text => "text",
            CommandResultFormat.Json => "json",
            CommandResultFormat.Markdown => "markdown",
            _ => "none"
        });
    }

    private static CommandResultFormat ReadStringValue(string? value)
    {
        if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResultFormat.Text;
        }

        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResultFormat.Json;
        }

        if (string.Equals(value, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResultFormat.Markdown;
        }

        return CommandResultFormat.None;
    }
}

#endregion

#region Wait For Resource

/// <summary>
/// Request to wait for a resource to reach a target status.
/// </summary>
internal sealed class WaitForResourceRequest : BackchannelRequest
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

    /// <inheritdoc />
    public override WaitForResourceRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        ResourceName = ResourceName,
        Status = Status,
        TimeoutSeconds = TimeoutSeconds
    };
}

/// <summary>
/// Response from waiting for a resource.
/// </summary>
internal sealed class WaitForResourceResponse
{
    /// <summary>
    /// Gets whether the resource reached the target status.
    /// Missing values from older payloads default to false so the wait fails cleanly.
    /// </summary>
    public bool Success { get; init; }

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
    private string[] _endpoints = [];
    private string _resource = string.Empty;
    private string _type = string.Empty;
    private string _state = string.Empty;

    /// <summary>
    /// Gets the name of the resource.
    /// </summary>
    public string Resource
    {
        get => _resource;
        init => _resource = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the type of the resource.
    /// </summary>
    public string Type
    {
        get => _type;
        init => _type = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the state of the resource.
    /// </summary>
    public string State
    {
        get => _state;
        init => _state = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the endpoints associated with the resource.
    /// </summary>
    public string[] Endpoints
    {
        get => _endpoints;
        init => _endpoints = value ?? [];
    }

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
    private string _type = string.Empty;
    private PublishingActivityData _data = new();
    private bool _hasData;

    /// <summary>
    /// Gets the type discriminator for the publishing activity.
    /// </summary>
    public string Type
    {
        get => _type;
        init => _type = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the data containing all properties for the publishing activity.
    /// </summary>
    public PublishingActivityData Data
    {
        get => _data;
        init
        {
            _data = value ?? new PublishingActivityData();
            _hasData = value is not null;
        }
    }

    internal bool HasData => _hasData;
}

/// <summary>
/// Common data for all publishing activities.
/// </summary>
internal sealed class PublishingActivityData
{
    private string _completionState = CompletionStates.InProgress;
    private string _id = string.Empty;
    private string _statusText = string.Empty;

    /// <summary>
    /// Gets the unique identifier for the publishing activity.
    /// </summary>
    public string Id
    {
        get => _id;
        init => _id = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the status text describing the publishing activity.
    /// </summary>
    public string StatusText
    {
        get => _statusText;
        init => _statusText = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the completion state of the publishing activity.
    /// </summary>
    public string CompletionState
    {
        get => _completionState;
        init => _completionState = value ?? CompletionStates.InProgress;
    }

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
    public IReadOnlyList<BackchannelPipelineSummaryItem?>? PipelineSummary { get; init; }

    /// <summary>
    /// Gets the input information for prompt activities, if available.
    /// </summary>
    public IReadOnlyList<PublishingPromptInput?>? Inputs { get; init; }

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
    private string _key = string.Empty;
    private string _value = string.Empty;

    /// <summary>
    /// Gets the key or label for the summary item.
    /// </summary>
    public string Key
    {
        get => _key;
        init => _key = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the string value for the summary item.
    /// </summary>
    public string Value
    {
        get => _value;
        init => _value = value ?? string.Empty;
    }

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
    private string _label = string.Empty;
    private string _inputType = string.Empty;

    /// <summary>
    /// Gets the name for the input.
    /// Nullable for backwards compatibility with Aspire 9.5 and older app hosts.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the label for the input.
    /// </summary>
    public string Label
    {
        get => _label;
        init => _label = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the type of the input.
    /// </summary>
    public string InputType
    {
        get => _inputType;
        init => _inputType = value ?? string.Empty;
    }

    /// <summary>
    /// Gets a value indicating whether the input is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Gets the options for the input. Only used by select inputs.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>?>? Options { get; init; }

    /// <summary>
    /// Gets the default value for the input.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets the validation errors for the input.
    /// </summary>
    public IReadOnlyList<string?>? ValidationErrors { get; init; }

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

internal sealed class BackchannelLogEntry
{
    private string _message = string.Empty;
    private string _categoryName = string.Empty;
    private bool _hasMessage;

    public required EventId EventId { get; init; }
    public required LogLevel LogLevel { get; init; }
    public string Message
    {
        get => _message;
        init
        {
            _hasMessage = value is not null;
            _message = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets whether <see cref="Message"/> was present on the wire; empty log messages are still valid.
    /// </summary>
    public bool HasMessage => _hasMessage;

    public required DateTimeOffset Timestamp { get; init; }
    public string CategoryName
    {
        get => _categoryName;
        init => _categoryName = value ?? string.Empty;
    }
}

internal sealed class PublishingPromptInputAnswer
{
    public string? Name { get; init; }
    public string? Value { get; init; }
}

/// <summary>
/// Represents metadata about a pipeline step for display purposes (e.g., --list-steps).
/// </summary>
internal sealed class PipelineStepInfo
{
    private string[] _dependsOn = [];
    private string[] _tags = [];
    private string _name = string.Empty;

    /// <summary>
    /// Gets the unique name of the step.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the description of the step.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the names of steps that this step depends on.
    /// </summary>
    public string[] DependsOn
    {
        get => _dependsOn;
        init => _dependsOn = value ?? [];
    }

    /// <summary>
    /// Gets the tags that categorize this step.
    /// </summary>
    public string[] Tags
    {
        get => _tags;
        init => _tags = value ?? [];
    }

    /// <summary>
    /// Gets the name of the resource this step is associated with, if any.
    /// </summary>
    public string? ResourceName { get; init; }
}

/// <summary>
/// Request for getting pipeline step metadata.
/// </summary>
internal sealed class GetPipelineStepsRequest : BackchannelRequest
{
    /// <summary>
    /// Gets or sets the target step name to filter to (including transitive dependencies).
    /// When null, all steps are returned.
    /// </summary>
    public string? Step { get; init; }

    /// <inheritdoc />
    public override GetPipelineStepsRequest WithTraceContext(BackchannelTraceContext traceContext) => new()
    {
        TraceContext = traceContext,
        Step = Step
    };
}

/// <summary>
/// Response containing pipeline step metadata.
/// </summary>
internal sealed class GetPipelineStepsResponse
{
    private PipelineStepInfo[] _steps = [];

    /// <summary>
    /// Gets the pipeline steps in topological (execution) order.
    /// </summary>
    public PipelineStepInfo[] Steps
    {
        get => _steps;
        init => _steps = value ?? [];
    }
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
    private ResourceSnapshotUrl[] _urls = [];
    private ResourceSnapshotRelationship[] _relationships = [];
    private ResourceSnapshotHealthReport[] _healthReports = [];
    private ResourceSnapshotVolume[] _volumes = [];
    private ResourceSnapshotEnvironmentVariable[] _environmentVariables = [];
    private Dictionary<string, string?> _properties = [];
    private ResourceSnapshotCommand[] _commands = [];
    private string _name = string.Empty;

    // Retired JSON properties:
    // - "Type": legacy name for "ResourceType"; keep the obsolete alias for old AppHost payloads.

    /// <summary>
    /// Gets the unique name of the resource.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

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
    public ResourceSnapshotUrl[] Urls
    {
        get => _urls;
        init => _urls = value ?? [];
    }

    /// <summary>
    /// Gets the relationships to other resources.
    /// </summary>
    public ResourceSnapshotRelationship[] Relationships
    {
        get => _relationships;
        init => _relationships = value ?? [];
    }

    /// <summary>
    /// Gets the health reports for this resource.
    /// </summary>
    public ResourceSnapshotHealthReport[] HealthReports
    {
        get => _healthReports;
        init => _healthReports = value ?? [];
    }

    /// <summary>
    /// Gets the volumes mounted to this resource.
    /// </summary>
    public ResourceSnapshotVolume[] Volumes
    {
        get => _volumes;
        init => _volumes = value ?? [];
    }

    /// <summary>
    /// Gets the environment variables for this resource.
    /// </summary>
    public ResourceSnapshotEnvironmentVariable[] EnvironmentVariables
    {
        get => _environmentVariables;
        init => _environmentVariables = value ?? [];
    }

    /// <summary>
    /// Gets additional properties as key-value pairs.
    /// This allows for extensibility without changing the schema.
    /// </summary>
    public Dictionary<string, string?> Properties
    {
        get => _properties;
        init => _properties = value ?? [];
    }

    /// <summary>
    /// Gets a value indicating whether this resource is hidden.
    /// </summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// Gets the MCP server information if the resource exposes an MCP endpoint.
    /// </summary>
    public ResourceSnapshotMcpServer? McpServer { get; init; }

    /// <summary>
    /// Gets the commands available for this resource.
    /// </summary>
    public ResourceSnapshotCommand[] Commands
    {
        get => _commands;
        init => _commands = value ?? [];
    }
}

/// <summary>
/// Represents a command available for a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, State = {State}")]
internal sealed class ResourceSnapshotCommand
{
    private ResourceSnapshotCommandArgument[] _argumentInputs = [];
    private string _visibility = KnownCommandVisibility.Default;
    private string _name = string.Empty;
    private string _state = string.Empty;

    /// <summary>
    /// Gets the command name (e.g., "start", "stop", "restart").
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the display name of the command.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the description of the command.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the ordered inputs that describe the invocation arguments accepted by the command.
    /// </summary>
    public ResourceSnapshotCommandArgument[] ArgumentInputs
    {
        get => _argumentInputs;
        init => _argumentInputs = value ?? [];
    }

    /// <summary>
    /// Gets where the command is visible to users and clients.
    /// </summary>
    public string Visibility
    {
        get => _visibility;
        init => _visibility = value ?? KnownCommandVisibility.Default;
    }

    /// <summary>
    /// Gets the state of the command (e.g., "Enabled", "Disabled", "Hidden").
    /// </summary>
    public string State
    {
        get => _state;
        init => _state = value ?? string.Empty;
    }
}

/// <summary>
/// Represents an invocation argument accepted by a resource command.
/// </summary>
internal sealed class ResourceSnapshotCommandArgument
{
    private string _name = string.Empty;
    private string _inputType = string.Empty;

    /// <summary>
    /// Gets the argument name.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the display label.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets the argument description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets a value indicating whether the description should be rendered as Markdown.
    /// </summary>
    public bool EnableDescriptionMarkdown { get; init; }

    /// <summary>
    /// Gets the input type.
    /// </summary>
    public string InputType
    {
        get => _inputType;
        init => _inputType = value ?? string.Empty;
    }

    /// <summary>
    /// Gets a value indicating whether the argument is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Gets the placeholder text.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Gets the default or submitted value.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets choice options keyed by submitted value.
    /// </summary>
    public Dictionary<string, string?>? Options { get; init; }

    /// <summary>
    /// Gets a value indicating whether custom choices are allowed.
    /// </summary>
    public bool AllowCustomChoice { get; init; }

    /// <summary>
    /// Gets a value indicating whether the argument input is disabled.
    /// </summary>
    public bool Disabled { get; init; }

    /// <summary>
    /// Gets the maximum length for text inputs.
    /// </summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// Represents a URL exposed by a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, Url = {Url}")]
internal sealed class ResourceSnapshotUrl
{
    private string _name = string.Empty;
    private string _url = string.Empty;

    /// <summary>
    /// Gets the URL name (e.g., "http", "https", "tcp").
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the full URL including scheme, host, and port.
    /// </summary>
    public string Url
    {
        get => _url;
        init => _url = value ?? string.Empty;
    }

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
    private string _resourceName = string.Empty;
    private string _type = string.Empty;

    /// <summary>
    /// Gets the name of the related resource.
    /// </summary>
    public string ResourceName
    {
        get => _resourceName;
        init => _resourceName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the relationship type (e.g., "Parent", "Reference").
    /// </summary>
    public string Type
    {
        get => _type;
        init => _type = value ?? string.Empty;
    }
}

/// <summary>
/// Represents a health report for a resource.
/// </summary>
[DebuggerDisplay("Name = {Name}, Status = {Status}")]
internal sealed class ResourceSnapshotHealthReport
{
    private string _name = string.Empty;

    /// <summary>
    /// Gets the name of the health check.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

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
    private string _target = string.Empty;
    private string _mountType = string.Empty;

    /// <summary>
    /// Gets the source path or volume name.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Gets the target path in the container.
    /// </summary>
    public string Target
    {
        get => _target;
        init => _target = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the mount type (e.g., "bind", "volume").
    /// </summary>
    public string MountType
    {
        get => _mountType;
        init => _mountType = value ?? string.Empty;
    }

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
    private string _name = string.Empty;

    /// <summary>
    /// Gets the name of the environment variable.
    /// </summary>
    public string Name
    {
        get => _name;
        init => _name = value ?? string.Empty;
    }

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
    private Tool[] _tools = [];
    private string _endpointUrl = string.Empty;

    /// <summary>
    /// Gets the MCP endpoint URL.
    /// </summary>
    public string EndpointUrl
    {
        get => _endpointUrl;
        init => _endpointUrl = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the tools exposed by the MCP server.
    /// </summary>
    public Tool[] Tools
    {
        get => _tools;
        init => _tools = value ?? [];
    }
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
    private string _resourceName = string.Empty;
    private string _content = string.Empty;
    private bool _hasContent;

    /// <summary>
    /// Gets the name of the resource that produced this log line.
    /// </summary>
    public string ResourceName
    {
        get => _resourceName;
        init => _resourceName = value ?? string.Empty;
    }

    /// <summary>
    /// Gets the line number within the log stream.
    /// </summary>
    public required int LineNumber { get; init; }

    /// <summary>
    /// Gets the content of the log line.
    /// </summary>
    public string Content
    {
        get => _content;
        init
        {
            _hasContent = value is not null;
            _content = value ?? string.Empty;
        }
    }

    /// <summary>
    /// Gets whether <see cref="Content"/> was present on the wire; empty resource log lines are still valid.
    /// </summary>
    public bool HasContent => _hasContent;

    /// <summary>
    /// Gets whether this log line is from stderr (error output).
    /// </summary>
    public bool IsError { get; init; }
}

/// <summary>
/// Represents a batch of resource console log lines.
/// </summary>
internal sealed class ResourceLogBatch
{
    /// <summary>
    /// Gets the log lines in this batch.
    /// </summary>
    public required ResourceLogLine[] Lines { get; init; }
}
