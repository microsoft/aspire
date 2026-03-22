// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Aspire.Cli.Commands;
using Aspire.Cli.Commands.Sdk;
using Aspire.TypeSystem;
using Spectre.Console;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using StreamJsonRpc.Reflection;

namespace Aspire.Cli.Backchannel;

[JsonSerializable(typeof(RuntimeSpec))]
[JsonSerializable(typeof(CommandSpec))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(DashboardUrlsState))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IAsyncEnumerable<RpcResourceState>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<RpcResourceState>))]
[JsonSerializable(typeof(IAsyncEnumerable<BackchannelLogEntry>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<BackchannelLogEntry>))]
[JsonSerializable(typeof(IAsyncEnumerable<PublishingActivity>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<PublishingActivity>))]
[JsonSerializable(typeof(RequestId))]
[JsonSerializable(typeof(IEnumerable<DisplayLineState>))]
[JsonSerializable(typeof(PublishingPromptInputAnswer[]))]
[JsonSerializable(typeof(ValidationResult))]
[JsonSerializable(typeof(IAsyncEnumerable<CommandOutput>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<CommandOutput>))]
[JsonSerializable(typeof(EnvVar))]
[JsonSerializable(typeof(List<EnvVar>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(DebugSessionOptions))]
[JsonSerializable(typeof(bool?))]
[JsonSerializable(typeof(AppHostProjectSearchResultPoco))]
[JsonSerializable(typeof(DashboardMcpConnectionInfo))]
[JsonSerializable(typeof(AppHostInformation))]
[JsonSerializable(typeof(ResourceSnapshot))]
[JsonSerializable(typeof(ResourceSnapshot[]))]
[JsonSerializable(typeof(List<ResourceSnapshot>))]
[JsonSerializable(typeof(IAsyncEnumerable<ResourceSnapshot>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<ResourceSnapshot>))]
[JsonSerializable(typeof(ResourceSnapshotMcpServer))]
[JsonSerializable(typeof(ResourceLogLine))]
[JsonSerializable(typeof(ResourceLogLine[]))]
[JsonSerializable(typeof(IAsyncEnumerable<ResourceLogLine>))]
[JsonSerializable(typeof(MessageFormatterEnumerableTracker.EnumeratorResults<ResourceLogLine>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(CapabilitiesInfo))]
[JsonSerializable(typeof(CommonErrorData))]
// V2 API request/response types
[JsonSerializable(typeof(GetCapabilitiesRequest))]
[JsonSerializable(typeof(GetCapabilitiesResponse))]
[JsonSerializable(typeof(GetAppHostInfoRequest))]
[JsonSerializable(typeof(GetAppHostInfoResponse))]
[JsonSerializable(typeof(GetDashboardInfoRequest))]
[JsonSerializable(typeof(GetDashboardInfoResponse))]
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
[JsonSerializable(typeof(WaitForResourceRequest))]
[JsonSerializable(typeof(WaitForResourceResponse))]
internal partial class BackchannelJsonSerializerContext : JsonSerializerContext
{
    [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode", Justification = "Using the Json source generator.")]
    [UnconditionalSuppressMessage("AotAnalysis", "IL3050:RequiresDynamicCode", Justification = "Using the Json source generator.")]
    internal static SystemTextJsonFormatter CreateRpcMessageFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions = CreateJsonSerializerOptions();
        return formatter;
    }

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
