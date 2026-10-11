// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Cli;

/// <summary>Describes the existing CLI run backchannel wire types.</summary>
internal sealed record NativeCliRequest
{
    public required string Jsonrpc { get; init; }
    public JsonElement? Id { get; init; }
    public required string Method { get; init; }
    public JsonElement? Params { get; init; }
    public string? Traceparent { get; init; }
    public string? Tracestate { get; init; }
}

internal sealed record NativeCliResponse
{
    public string Jsonrpc { get; init; } = "2.0";
    public required JsonElement Id { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public NativeCliError? Error { get; init; }
}

internal sealed record NativeCliError(int Code, string Message);
internal sealed record NativeCliStreamDescriptor(long Token);
internal sealed record NativeCliEnumeratorResult(JsonElement[] Values, bool Finished);
internal sealed record NativeCliCancellation(JsonElement Id);
internal sealed record NativeCliDashboardUrls
{
    public bool DashboardHealthy { get; init; } = true;
    public string? BaseUrlWithLoginToken { get; init; }
    public string? CodespacesUrlWithLoginToken { get; init; }
}
internal sealed record NativeCliResourceState(string Resource, string Type, string State, string[] Endpoints, string? Health);
internal sealed record NativeCliLogEntry(long SequenceNumber, Guid GenerationId, EventId EventId,
    LogLevel LogLevel, string Message, DateTimeOffset Timestamp, string CategoryName);

[JsonSerializable(typeof(NativeCliRequest))]
[JsonSerializable(typeof(NativeCliResponse))]
[JsonSerializable(typeof(NativeCliCancellation))]
[JsonSerializable(typeof(NativeCliStreamDescriptor))]
[JsonSerializable(typeof(NativeCliEnumeratorResult))]
[JsonSerializable(typeof(NativeCliDashboardUrls))]
[JsonSerializable(typeof(NativeCliResourceState))]
[JsonSerializable(typeof(NativeCliLogEntry))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
internal sealed partial class NativeCliJsonContext : JsonSerializerContext;
