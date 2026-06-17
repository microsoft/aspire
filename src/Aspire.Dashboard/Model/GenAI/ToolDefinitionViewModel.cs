// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Dashboard.Model.GenAI;

[DebuggerDisplay("ToolDefinition = {ToolDefinition}, Expanded = {Expanded}")]
public class ToolDefinitionViewModel
{
    public required ToolDefinition ToolDefinition { get; init; }
    public bool Expanded { get; set; }
    public bool Highlighted { get; set; }
    public string ElementId { get; } = $"tool-def-{Guid.NewGuid():N}";
}
