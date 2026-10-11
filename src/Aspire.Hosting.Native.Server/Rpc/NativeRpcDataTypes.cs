// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Native.Rpc;

internal sealed record NativeRpcResponse
{
    public string Jsonrpc { get; init; } = "2.0";
    public JsonNode? Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeRpcError? Error { get; init; }
}

internal sealed record NativeRpcError(int Code, string Message, NativeRpcErrorData Data);
internal sealed record NativeRpcErrorData(string Code);
internal sealed record NativeRpcCapabilityError(string Code, string Message);
internal sealed record NativeRpcCapabilityFailure([property: JsonPropertyName("$error")] NativeRpcCapabilityError Error);

[JsonSerializable(typeof(NativeRpcResponse))]
[JsonSerializable(typeof(NativeRpcCapabilityFailure))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class NativeRpcMessageJsonContext : JsonSerializerContext;
