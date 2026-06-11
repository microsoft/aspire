// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aspire.Dashboard.Model.GenAI;

public sealed class SystemOrUserEvent
{
    public string? Role { get; set; }
    public string? Content { get; set; }
}

public sealed class AssistantEvent
{
    public string? Content { get; set; }
    public ToolCall[]? ToolCalls { get; set; }
}

public sealed class ToolEvent
{
    public string? Id { get; set; }
    public JsonNode? Content { get; set; }
}

public sealed class ChoiceEvent
{
    public string? FinishReason { get; set; }
    public int Index { get; set; }
    public AssistantEvent? Message { get; set; }
}

public sealed class ToolCall
{
    public string? Id { get; set; }
    public string? Type { get; set; } = "function";
    public ToolCallFunction? Function { get; set; }
}

public sealed class ToolCallFunction
{
    public string? Name { get; set; }
    public JsonNode? Arguments { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(SystemOrUserEvent))]
[JsonSerializable(typeof(AssistantEvent))]
[JsonSerializable(typeof(ToolEvent))]
[JsonSerializable(typeof(ChoiceEvent))]
public sealed partial class GenAIEventsContext : JsonSerializerContext;
