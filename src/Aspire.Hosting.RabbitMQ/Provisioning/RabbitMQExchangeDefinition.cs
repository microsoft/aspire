// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// Declared-field view of an exchange from <c>GET /api/exchanges/{vhost}/{name}</c>, used for drift comparison.
/// </summary>
/// <remarks>
/// Only the fields Aspire authored in its own declare are mapped here. The management API response also
/// includes server-computed fields such as <c>effective_policy_definition</c> and applied-policy lists.
/// Those are deliberately NOT declared as properties: drift detection is scoped to declared fields only,
/// so any un-mapped JSON is ignored on deserialization. Reproducing the server's policy resolution
/// inside Aspire is explicitly out of scope.
/// </remarks>
internal sealed record RabbitMQExchangeDefinition(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("durable")] bool Durable,
    [property: JsonPropertyName("auto_delete")] bool AutoDelete,
    [property: JsonPropertyName("arguments")] IDictionary<string, object?>? Arguments);
