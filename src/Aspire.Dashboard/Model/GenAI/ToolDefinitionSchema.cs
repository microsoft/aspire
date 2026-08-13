// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace Aspire.Dashboard.Model.GenAI;

internal sealed class ToolDefinitionSchema
{
    public JsonSchemaType? Type { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, ToolDefinitionSchema>? Properties { get; set; }
    public ToolDefinitionSchema? Items { get; set; }
    public HashSet<string>? Required { get; set; }
    public List<JsonNode>? Enum { get; set; }
}

[Flags]
internal enum JsonSchemaType
{
    Null = 1,
    Boolean = 2,
    Integer = 4,
    Number = 8,
    String = 16,
    Object = 32,
    Array = 64,
}
