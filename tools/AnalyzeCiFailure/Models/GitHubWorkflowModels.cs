// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Reads the agent-output envelope used to find and validate structured rerun requests.</summary>
internal sealed record WorkflowAgentOutput
{
    public required JsonElement Items { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}

/// <summary>Binds an agent rerun request to the exact run and subject-PR context checked by the rerunner.</summary>
internal sealed record WorkflowRerunRequest
{
    public required string Type { get; init; }
    public required long RunId { get; init; }
    public string? PrNumbers { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; init; }
}
