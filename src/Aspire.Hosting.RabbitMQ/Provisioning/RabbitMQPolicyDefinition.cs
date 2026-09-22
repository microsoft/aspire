// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// JSON payload for <c>PUT /api/policies/{vhost}/{name}</c>.
/// </summary>
internal sealed record RabbitMQPolicyDefinition(
    [property: JsonPropertyName("pattern")] string Pattern,
    [property: JsonPropertyName("apply-to")] string ApplyTo,
    [property: JsonPropertyName("definition")] IDictionary<string, object?> Definition,
    [property: JsonPropertyName("priority")] int Priority);
