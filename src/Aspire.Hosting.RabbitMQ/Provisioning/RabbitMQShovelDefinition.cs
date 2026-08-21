// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal sealed record RabbitMQShovelDefinition
{
    [JsonPropertyName("value")]
    public required RabbitMQShovelDefinitionValue Value { get; init; }
}

internal sealed record RabbitMQShovelDefinitionValue
{
    [JsonPropertyName("src-uri")]
    public required string SrcUri { get; init; }

    [JsonPropertyName("src-queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SrcQueue { get; init; }

    [JsonPropertyName("src-exchange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SrcExchange { get; init; }

    [JsonPropertyName("src-exchange-key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SrcExchangeKey { get; init; }

    [JsonPropertyName("dest-uri")]
    public required string DestUri { get; init; }

    [JsonPropertyName("dest-queue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DestQueue { get; init; }

    [JsonPropertyName("dest-exchange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DestExchange { get; init; }

    [JsonPropertyName("dest-exchange-key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DestExchangeKey { get; init; }

    [JsonPropertyName("ack-mode")]
    public required string AckMode { get; init; }

    [JsonPropertyName("reconnect-delay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReconnectDelay { get; init; }

    [JsonPropertyName("src-delete-after")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SrcDeleteAfter { get; init; }
}
