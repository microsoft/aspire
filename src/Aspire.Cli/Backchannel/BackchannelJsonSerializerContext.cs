// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Aspire.Cli.Commands;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Projects;
using Aspire.TypeSystem;
using Spectre.Console;

namespace Aspire.Cli.Backchannel;

[JsonSerializable(typeof(RuntimeSpec))]
[JsonSerializable(typeof(CommandSpec))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(DashboardUrlsState))]
[JsonSerializable(typeof(JsonElement))]
// CurlyRpc streams plain IAsyncEnumerable<T>, serializing each element T directly (no
// EnumeratorResults<T> wrapper type). The element types below were previously pulled in
// transitively by the SJR MessageFormatterEnumerableTracker.EnumeratorResults<T> registrations,
// so register them (and their batch collection shapes) explicitly for the AOT source generator.
[JsonSerializable(typeof(RpcResourceState))]
[JsonSerializable(typeof(RpcResourceState[]))]
[JsonSerializable(typeof(List<RpcResourceState>))]
[JsonSerializable(typeof(IAsyncEnumerable<RpcResourceState>))]
[JsonSerializable(typeof(BackchannelLogEntry))]
[JsonSerializable(typeof(BackchannelLogEntry[]))]
[JsonSerializable(typeof(List<BackchannelLogEntry>))]
[JsonSerializable(typeof(IAsyncEnumerable<BackchannelLogEntry>))]
[JsonSerializable(typeof(PublishingActivity))]
[JsonSerializable(typeof(PublishingActivity[]))]
[JsonSerializable(typeof(List<PublishingActivity>))]
[JsonSerializable(typeof(IAsyncEnumerable<PublishingActivity>))]
[JsonSerializable(typeof(IEnumerable<DisplayLineState>))]
[JsonSerializable(typeof(PublishingPromptInputAnswer[]))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(EnvVar))]
[JsonSerializable(typeof(List<EnvVar>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(DebugSessionOptions))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(AppHostProjectSearchResultPoco))]
[JsonSerializable(typeof(AppHostInformation))]
[JsonSerializable(typeof(ResourceSnapshot))]
[JsonSerializable(typeof(ResourceSnapshot[]))]
[JsonSerializable(typeof(List<ResourceSnapshot>))]
[JsonSerializable(typeof(IAsyncEnumerable<ResourceSnapshot>))]
[JsonSerializable(typeof(ResourceSnapshotCommandArgument))]
[JsonSerializable(typeof(ResourceSnapshotCommandArgument[]))]
[JsonSerializable(typeof(ResourceSnapshotMcpServer))]
[JsonSerializable(typeof(ResourceLogLine))]
[JsonSerializable(typeof(ResourceLogLine[]))]
[JsonSerializable(typeof(IAsyncEnumerable<ResourceLogLine>))]
[JsonSerializable(typeof(ResourceLogBatch))]
[JsonSerializable(typeof(ResourceLogBatch[]))]
[JsonSerializable(typeof(IAsyncEnumerable<ResourceLogBatch>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonNode?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(CapabilitiesInfo))]
[JsonSerializable(typeof(AppHostCodeGenerationDiagnostic))]
[JsonSerializable(typeof(AppHostLoadedAssemblyInfo))]
[JsonSerializable(typeof(List<AppHostLoadedAssemblyInfo>))]
// V2 API request/response types
[JsonSerializable(typeof(GetCapabilitiesRequest))]
[JsonSerializable(typeof(BackchannelTraceContext))]
[JsonSerializable(typeof(GetCapabilitiesResponse))]
[JsonSerializable(typeof(GetAppHostInfoRequest))]
[JsonSerializable(typeof(GetAppHostInfoResponse))]
[JsonSerializable(typeof(GetDashboardInfoRequest))]
[JsonSerializable(typeof(GetDashboardInfoResponse))]
[JsonSerializable(typeof(WaitForAppHostReadyRequest))]
[JsonSerializable(typeof(WaitForAppHostReadyResponse))]
[JsonSerializable(typeof(GetResourcesRequest))]
[JsonSerializable(typeof(GetResourcesResponse))]
[JsonSerializable(typeof(WatchResourcesRequest))]
[JsonSerializable(typeof(GetConsoleLogsRequest))]
[JsonSerializable(typeof(CallMcpToolRequest))]
[JsonSerializable(typeof(CallMcpToolResponse))]
[JsonSerializable(typeof(McpToolContentItem))]
[JsonSerializable(typeof(McpToolContentItem[]))]
[JsonSerializable(typeof(StopAppHostRequest))]
[JsonSerializable(typeof(StopAppHostResponse))]
[JsonSerializable(typeof(ExecuteResourceCommandRequest))]
[JsonSerializable(typeof(ExecuteResourceCommandResponse))]
[JsonSerializable(typeof(ResourceCommandArgumentValidationError))]
[JsonSerializable(typeof(WaitForResourceRequest))]
[JsonSerializable(typeof(WaitForResourceResponse))]
[JsonSerializable(typeof(PipelineStepInfo))]
[JsonSerializable(typeof(PipelineStepInfo[]))]
[JsonSerializable(typeof(GetPipelineStepsRequest))]
[JsonSerializable(typeof(GetPipelineStepsResponse))]
[JsonSerializable(typeof(GetTerminalInfoRequest))]
[JsonSerializable(typeof(GetTerminalInfoResponse))]
[JsonSerializable(typeof(TerminalReplicaInfo))]
[JsonSerializable(typeof(TerminalReplicaInfo[]))]
[JsonSerializable(typeof(TerminalPeerInfo))]
[JsonSerializable(typeof(TerminalPeerInfo[]))]
[JsonSerializable(typeof(ListTerminalsRequest))]
[JsonSerializable(typeof(ListTerminalsResponse))]
[JsonSerializable(typeof(TerminalSummary))]
[JsonSerializable(typeof(TerminalSummary[]))]
internal partial class BackchannelJsonSerializerContext : JsonSerializerContext
{
    internal static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var options = new JsonSerializerOptions(ModelContextProtocol.McpJsonUtilities.DefaultOptions);
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            Default,
            ModelContextProtocol.McpJsonUtilities.DefaultOptions.TypeInfoResolver
        );
        return options;
    }
}
