// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Native.Dcp;

internal sealed record DcpResourceEnvelope(string ApiVersion, string Kind, DcpMetadata Metadata, JsonElement Spec);
internal sealed record DcpMetadata(string Name, Dictionary<string, string>? Annotations);
internal sealed record DcpResource(DcpStatus? Status);
internal sealed record DcpStatus
{
    public string? State { get; init; }
    public string? ContainerId { get; init; }
    public long? Pid { get; init; }
    public int? ExitCode { get; init; }
    public int? EffectivePort { get; init; }
    public string? EffectiveAddress { get; init; }
}
internal sealed record DcpServiceSpec(string Protocol, string AddressAllocationMode);
internal sealed record DcpServiceProducer(string ServiceName, string Address, int? Port);
internal sealed record DcpEnvironment(string Name, string Value);
internal sealed record DcpContainerPort(int ContainerPort, string HostIP, string Protocol);
internal sealed record DcpWorkloadSpec
{
    public required DcpEnvironment[] Env { get; init; }
    public required string[] Args { get; init; }
    public string? Image { get; init; }
    public DcpContainerPort[]? Ports { get; init; }
    public string? ExecutablePath { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ExecutionType { get; init; }
}
internal sealed record DcpExecutionRequest(string Status, string ShutdownResourceCleanup);
internal sealed record DcpExecutionResponse(string Status);

[JsonSerializable(typeof(DcpResourceEnvelope))]
[JsonSerializable(typeof(DcpResource))]
[JsonSerializable(typeof(DcpServiceSpec))]
[JsonSerializable(typeof(DcpServiceProducer[]))]
[JsonSerializable(typeof(DcpWorkloadSpec))]
[JsonSerializable(typeof(DcpExecutionRequest))]
[JsonSerializable(typeof(DcpExecutionResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true)]
internal sealed partial class DcpJsonContext : JsonSerializerContext;
